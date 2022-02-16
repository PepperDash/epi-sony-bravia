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
        private readonly IBasicCommunication _coms;
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

            _coms = comms;
            var powerQuery = Commands.GetPowerQuery(_coms);
            var inputQuery = Commands.GetInputQuery(_coms);

            _powerOnCommand = Commands.GetPowerOn(_coms);
            _powerOffCommand = Commands.GetPowerOff(_coms);

            var props = config.Properties.ToObject<SonyBraviaConfig>();
            var monitorConfig = props.CommunicationMonitorProperties ?? DefaultMonitorConfig;

            CommunicationMonitor = new GenericCommunicationMonitor(
                this, _coms, monitorConfig.PollInterval, monitorConfig.TimeToWarning, monitorConfig.TimeToError,
                () => CommandQueue.Enqueue(powerQuery));

            InputPorts.AddRange(RoutingInputPorts.Build(this, _coms));

            var worker = new Thread(ProcessResponseQueue, null);
            _pollTimer = new CTimer(Poll, new[] {inputQuery, powerQuery}, Timeout.Infinite);

            _coms.BytesReceived += (sender, args) => _queue.Enqueue(args.Bytes);

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

        public void InputHdmi1()
        {
            CommandQueue.Enqueue(Commands.GetHdmi1(_coms));
        }

        public void InputHdmi2()
        {
            CommandQueue.Enqueue(Commands.GetHdmi2(_coms));
        }

        public void InputHdmi3()
        {
            CommandQueue.Enqueue(Commands.GetHdmi3(_coms));
        }

        public void InputHdmi4()
        {
            CommandQueue.Enqueue(Commands.GetHdmi4(_coms));
        }

        public void InputHdmi5()
        {
            CommandQueue.Enqueue(Commands.GetHdmi5(_coms));
        }

        public void InputVideo1()
        {
            CommandQueue.Enqueue(Commands.GetVideo1(_coms));
        }

        public void InputVideo2()
        {
            CommandQueue.Enqueue(Commands.GetVideo2(_coms));
        }

        public void InputVideo3()
        {
            CommandQueue.Enqueue(Commands.GetVideo3(_coms));
        }

        public void InputComponent1()
        {
            CommandQueue.Enqueue(Commands.GetComponent1(_coms));
        }

        public void InputComponent2()
        {
            CommandQueue.Enqueue(Commands.GetComponent2(_coms));
        }

        public void InputComponent3()
        {
            CommandQueue.Enqueue(Commands.GetComponent3(_coms));
        }

        public void InputPc()
        {
            CommandQueue.Enqueue(Commands.GetPc(_coms));
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