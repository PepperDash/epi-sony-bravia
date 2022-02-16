using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.Queues;
using PepperDash.Essentials.Devices.Displays;

namespace SonyBraviaEpi
{
    public class SonyBraviaDevice : TwoWayDisplayBase, ICommunicationMonitor, IBridgeAdvanced, 
        IInputHdmi1, IInputHdmi2, IInputHdmi3, IInputHdmi4, IInputVga1,
        IOnline 
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
        private bool _isCooling;
        private bool _isWarming;
        private readonly long _coolingTimeMs;
        private readonly long _warmingtimeMs;        

        public SonyBraviaDevice(DeviceConfig config, IBasicCommunication comms) 
            : base(config.Key, config.Name)
        {
            if (CommandQueue == null)
                CommandQueue = new GenericQueue("SonyBraviaCommandQueue", 50);

            _coms = comms;
            var powerQuery = Commands.GetPowerQuery(_coms);
            var inputQuery = Commands.GetInputQuery(_coms);

            _powerOnCommand = Commands.GetPowerOn(_coms);
            _powerOffCommand = Commands.GetPowerOff(_coms);

            var props = config.Properties.ToObject<SonyBraviaConfig>();
            _coolingTimeMs = props.CoolingTimeMs > 9999 ? props.CoolingTimeMs : 20000;
            _warmingtimeMs = props.WarmingTimeMs > 9999 ? props.WarmingTimeMs : 20000;

            var monitorConfig = props.CommunicationMonitorProperties ?? DefaultMonitorConfig;

            CommunicationMonitor = new GenericCommunicationMonitor(
                this, _coms, monitorConfig.PollInterval, monitorConfig.TimeToWarning, monitorConfig.TimeToError,
                () => CommandQueue.Enqueue(powerQuery));

            //InputPorts.AddRange(RoutingInputPorts.Build(this, _coms));
            InitializeInputRoutingPorts();

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

        public bool PowerIsOn
        {
            get { return _powerIsOn; }
            set
            {
                _powerIsOn = value;
                if (_powerIsOn)
                {
                    IsWarming = true;

                    WarmupTimer = new CTimer(o =>
                    {
                        IsWarming = false;
                    }, _warmingtimeMs);
                }
                else
                {
                    IsCooling = true;

                    CooldownTimer = new CTimer(o =>
                    {
                        IsCooling = false;
                    }, _coolingTimeMs);
                }
            }
        }
        
        public bool IsCooling
        {
            get { return _isCooling; }
            set
            {
                _isCooling = value;
                IsCoolingDownFeedback.FireUpdate();
            }
        }

        public bool IsWarming
        {
            get { return _isWarming; }
            set
            {
                _isWarming = value;
                IsWarmingUpFeedback.FireUpdate();
            }
        }

        protected override Func<bool> IsCoolingDownFeedbackFunc
        {
            get { return () => IsCooling; }
        }

        protected override Func<bool> IsWarmingUpFeedbackFunc
        {
            get { return () => IsWarming; }
        }

        protected override Func<string> CurrentInputFeedbackFunc
        {
            get { return () => _currentInput; }
        }

        protected override Func<bool> PowerIsOnFeedbackFunc
        {
            get { return () => PowerIsOn; }
        }        

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

        private void AddInputRoutingPort(RoutingInputPort input, int port)
        {            
            input.Port = port;
            InputPorts.Add(input);
        }

        public void ListRoutingInputPorts()
        {
            var seperator = new string('*', 50);

            Debug.Console(0, this, seperator);
            foreach (var inputPort in InputPorts)
            {
                Debug.Console(0, this, "inputPort key: {0}, connectionType: {1}, feedbackMatchObject: {2}, port: {3}",
                    inputPort.Key, inputPort.ConnectionType, inputPort.FeedbackMatchObject, inputPort.Port);
            }
            Debug.Console(0, this, seperator);
        }

        public void InitializeInputRoutingPorts()
        {
            AddInputRoutingPort(new RoutingInputPort(
                    "hdmi1", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi, 
                    new Action(InputHdmi1), this), 1);

            AddInputRoutingPort(new RoutingInputPort(
                    "hdmi2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi, 
                    new Action(InputHdmi2), this), 2);

            AddInputRoutingPort(new RoutingInputPort(
                    "hdmi3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi, 
                    new Action(InputHdmi3), this), 3);

            AddInputRoutingPort(new RoutingInputPort(
                    "hdmi4", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi, 
                    new Action(InputHdmi4), this), 4);

            AddInputRoutingPort(new RoutingInputPort(
                    "hdmi5", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi, 
                    new Action(InputHdmi5), this), 5);

            AddInputRoutingPort(new RoutingInputPort(
                    "pc", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Vga, 
                    new Action(InputVga1), this), 6);

            AddInputRoutingPort(new RoutingInputPort(
                    "video1", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite, 
                    new Action(InputVideo1), this), 7);

            AddInputRoutingPort(new RoutingInputPort(
                    "video2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite, 
                    new Action(InputVideo2), this), 8);

            AddInputRoutingPort(new RoutingInputPort(
                    "video3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo3), this), 9);

            AddInputRoutingPort(new RoutingInputPort(
                    "component1", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component, 
                    new Action(InputVideo3), this), 10);

            AddInputRoutingPort(new RoutingInputPort(
                    "component2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component, 
                    new Action(InputComponent2), this), 11);

            AddInputRoutingPort(new RoutingInputPort(
                    "component3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component, 
                    new Action(InputComponent3), this), 12);
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

        public void InputVga1()
        {
            CommandQueue.Enqueue(Commands.GetPc(_coms));
        }

        public override void ExecuteSwitch(object selector)
        {
            if (PowerIsOn)
            {
                var action = selector as Action;
                if (action == null) return;

                action();
            }
            else
            {
                EventHandler<FeedbackEventArgs> handler = null;
                handler = (sender, args) =>
                {
                    if (IsWarming)
                        return;

                    IsWarmingUpFeedback.OutputChange -= handler;

                    var action = selector as Action;
                    if (action == null) return;

                    action();
                };

                IsWarmingUpFeedback.OutputChange += handler;
                PowerOn();
            }
        }

        private object ProcessResponseQueue(object _)
        {
            byte[] buffer = null;
            while (true)
            {
                try
                {
                    var bytes = _queue.Dequeue();
                    if (bytes == null)
                        return null;

                    Debug.Console(DebugLevels.Debug, this, "Processing Response: {0}", bytes.ToReadableString());

                    if (buffer == null)
                        buffer = bytes;
                    else
                    {
                        var newBuffer = new byte[buffer.Length + bytes.Length];
                        buffer.CopyTo(newBuffer, 0);
                        bytes.CopyTo(newBuffer, buffer.Length);
                        buffer = newBuffer;
                    }

                    Debug.Console(DebugLevels.Debug, this, "Processing Buffer: {0}", buffer.ToReadableString());

                    if (!buffer.ContainsHeader())
                        continue;

                    if (buffer.ElementAtOrDefault(0) != 0x70)
                        buffer = buffer.CleanToFirstHeader();

                    while (buffer.Length >= 4)
                    {
                        var message = buffer.GetFirstMessage();
                        //Debug.Console(DebugLevels.Debug, this, "Message.GetFirstMessage: {0}", message.ToReadableString());

                        if (message.Length < 4)
                        {
                            // we have an ACK in here, let's print it out and keep moving                            
                            switch (message.ToReadableString())
                            {
                                // response to query request (abnormal end) - Command Cancelled
                                // package is recieved normally, but the request is not acceptable in the current display status
                                case "70-03-74":
                                {
                                    Debug.Console(DebugLevels.Debug, this,"Found Abnormal End Response, Command Cancelled: {0}", message.ToReadableString());                            
                                    break;
                                }
                                // response to query request (abnormal end) - ParseError (Data Format Error)
                                case "70-04-74":
                                {
                                    Debug.Console(DebugLevels.Debug, this, "Found Abnormal End Response, Parse Error (Data Format Error): {0}", message.ToReadableString());
                                    break;
                                }
                                default:
                                {
                                    Debug.Console(DebugLevels.Debug, this, "Found Unknown Response Type: {0}", message.ToReadableString());
                                    break;
                                }
                            }
                            
                            buffer = buffer.CleanOutFirstMessage();
                            continue;
                        }

                        // we have a full message, lets check it out
                        Debug.Console(DebugLevels.Debug, this, "Processing Message: {0}", message.ToReadableString());

                        var dataSize = message[2];
                        var totalDataSize = dataSize + 3;
                        var isComplate = totalDataSize == message.Length;
                        Debug.Console(
                            DebugLevels.Debug, this, "Data Size: {0} | Total Data Size: {1} | Message Size: {2}", dataSize,
                            totalDataSize, message.Length);

                        if (!isComplate)
                        {
                            Debug.Console(DebugLevels.Debug, this, "Message is incomplete... spinning around");
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
                    Debug.Console(DebugLevels.Info, this, Debug.ErrorLogLevel.Notice, "ProcessResponseQueue Exception: {0}",ex.Message);
                    Debug.Console(DebugLevels.Debug, this, Debug.ErrorLogLevel.Notice, "ProcessResponseQueue Exception Stack Trace: {0}", ex.StackTrace);
                    if(ex.InnerException != null)
                        Debug.Console(DebugLevels.Verbose, this, Debug.ErrorLogLevel.Notice, "ProcessResponseQueue Inner Exception: {0}", ex.InnerException);
                }
            }
        }
    }
}