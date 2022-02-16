using System;
using System.Collections.Generic;
using System.Linq;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.Queues;

namespace SonyBraviaEpi
{
    public class SonyBraviaDevice : TwoWayDisplayBase, ICommunicationMonitor, IBridgeAdvanced, IOnline
    {
        public static GenericQueue CommandQueue;

        public static readonly CommunicationMonitorConfig DefaultMonitorConfig = new CommunicationMonitorConfig
        {
            PollInterval = 30000,
            TimeToWarning = 60000,
            TimeToError = 120000
        };

        private readonly CTimer _pollTimer;
        private readonly IQueueMessage _powerOffCommand;
        private readonly IQueueMessage _powerOnCommand;

        private readonly CrestronQueue<byte[]> _queue = new CrestronQueue<byte[]>(50);
        private string _currentInput;
        private bool _powerIsOn;

        public SonyBraviaDevice(DeviceConfig config, IBasicCommunication comms) : base(config.Key, config.Name)
        {
            if (CommandQueue == null)
                CommandQueue = new GenericQueue("SonyBraviaCommandQueue", 50);

            var coms = comms;
            var powerQuery = Commands.GetPowerQuery(coms);
            var inputQuery = Commands.GetInputQuery(coms);

            _powerOnCommand = Commands.GetPowerOn(coms);
            _powerOffCommand = Commands.GetPowerOff(coms);

            var props = config.Properties.ToObject<SonyBraviaConfig>();
            var monitorConfig = props.CommunicationMonitorProperties ?? DefaultMonitorConfig;

            CommunicationMonitor = new GenericCommunicationMonitor(
                this, coms, monitorConfig.PollInterval, monitorConfig.TimeToWarning, monitorConfig.TimeToError,
                () => CommandQueue.Enqueue(powerQuery));

            InputPorts.AddRange(RoutingInputPorts.Build(this, coms));

            var worker = new Thread(ProcessResponseQueue, null);
            _pollTimer = new CTimer(Poll, new[] {inputQuery, powerQuery}, Timeout.Infinite);

            coms.BytesReceived += (sender, args) => _queue.Enqueue(args.Bytes);

            CrestronEnvironment.ProgramStatusEventHandler += type =>
            {
                try
                {
                    if (type != eProgramStatusEventType.Stopping)
                        return;

                    _pollTimer.Stop();
                    _pollTimer.Dispose();
                    _queue.Enqueue(null);
                    worker.Join();
                }
                catch (Exception ex)
                {
                    Debug.Console(
                        1, this, Debug.ErrorLogLevel.Notice, "Caught an exception at program stop: {0}{1}",
                        ex.Message, ex.StackTrace);
                }
            };

            DeviceManager.AllDevicesActivated += (sender, args) =>
            {
                try
                {
                    CommunicationMonitor.Start();
                    _pollTimer.Reset(5000, 5000);
                }
                catch (Exception ex)
                {
                    Debug.Console(
                        1, this, Debug.ErrorLogLevel.Notice, "Caught an exception at AllDevicesActivated: {0}{1}",
                        ex.Message, ex.StackTrace);
                }
            };
        }

        protected override Func<bool> IsCoolingDownFeedbackFunc { get { return () => false; } }

        protected override Func<bool> IsWarmingUpFeedbackFunc { get { return () => false; } }

        protected override Func<string> CurrentInputFeedbackFunc { get { return () => _currentInput; } }

        protected override Func<bool> PowerIsOnFeedbackFunc { get { return () => _powerIsOn; } }

        public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            LinkDisplayToApi(this, trilist, joinStart, joinMapKey, bridge);
        }

        public StatusMonitorBase CommunicationMonitor { get; private set; }

        public BoolFeedback IsOnline { get { return CommunicationMonitor.IsOnlineFeedback; } }

        public static void Poll(object o)
        {
            var commands = o as IEnumerable<IQueueMessage>;
            if (commands == null)
                return;

            commands.ToList().ForEach(CommandQueue.Enqueue);
        }

        public override void PowerOn()
        {
            CommandQueue.Enqueue(_powerOnCommand);
            _pollTimer.Reset(1000, 5000);
        }

        public override void PowerOff()
        {
            CommandQueue.Enqueue(_powerOffCommand);
            _pollTimer.Reset(1000, 5000);
        }

        public override void PowerToggle()
        {
            if (_powerIsOn)
            {
                PowerOff();
            }
            else
            {
                PowerOn();
            }
        }

        public override void ExecuteSwitch(object selector)
        {
            var a = selector as Action;
            if (a == null)
                return;

            a();
        }

        private object ProcessResponseQueue(object _)
        {
            const int PARSING_DEBUG = 0;

            byte[] buffer = null;
            while (true)
            {
                try
                {
                    var bytes = _queue.Dequeue();
                    if (bytes == null)
                        return null;

                    Debug.Console(PARSING_DEBUG, this, "Processing Response: {0}", bytes.ToReadableString());

                    if (buffer == null)
                        buffer = bytes;
                    else
                    {
                        var newBuffer = new byte[buffer.Length + bytes.Length];
                        buffer.CopyTo(newBuffer, 0);
                        bytes.CopyTo(newBuffer, buffer.Length);
                        buffer = newBuffer;
                    }

                    Debug.Console(PARSING_DEBUG, this, "Processing Buffer: {0}", buffer.ToReadableString());

                    if (!buffer.ContainsHeader())
                        continue;

                    if (buffer.ElementAtOrDefault(0) != 0x70)
                        buffer = buffer.CleanToFirstHeader();

                    while (buffer.Length >= 4)
                    {
                        var message = buffer.GetFirstMessage();
                        if (message.Length < 4)
                        {
                            // we have an ACK in here, let's print it out and keep moving
                            Debug.Console(PARSING_DEBUG, this, "Found an ACK/NACK: {0}", message.ToReadableString());
                            buffer = buffer.CleanOutFirstMessage();
                            continue;
                        }

                        // we have a full message, lets check it out
                        Debug.Console(PARSING_DEBUG, this, "Processing Message: {0}", message.ToReadableString());

                        var dataSize = message[2];
                        var totalDataSize = dataSize + 3;
                        var isComplate = totalDataSize == message.Length;
                        Debug.Console(
                            PARSING_DEBUG, this, "Data Size: {0} | Total Data Size: {1} | Message Size: {2}", dataSize,
                            totalDataSize, message.Length);

                        if (!isComplate)
                        {
                            Debug.Console(PARSING_DEBUG, this, "Message is incomplete... spinning around");
                            break;
                        }

                        bool powerResult;
                        if (buffer.ParsePowerResponse(out powerResult))
                        {
                            _powerIsOn = powerResult;
                            PowerIsOnFeedback.FireUpdate();
                        }

                        string input;
                        if (buffer.ParseInputResponse(out input))
                        {
                            _currentInput = input;
                            CurrentInputFeedback.FireUpdate();
                        }

                        buffer = buffer.NumberOfHeaders() > 1 ? buffer.CleanOutFirstMessage() : new byte[0];
                    }
                }
                catch (Exception ex)
                {
                    Debug.Console(
                        1, this, Debug.ErrorLogLevel.Notice, "Caught an exception processing the string : {0}{1}",
                        ex.Message, ex.StackTrace);
                }
            }
        }
    }
}