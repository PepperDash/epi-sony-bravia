using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.Queues;
using PepperDash.Essentials.Core.Routing;
using PepperDash.Essentials.Devices.Displays;

namespace SonyBraviaEpi
{
    public class SonyBraviaDevice : TwoWayDisplayBase, ICommunicationMonitor, IBridgeAdvanced,
        IInputHdmi1, IInputHdmi2, IInputHdmi3, IInputHdmi4, IInputVga1,
        IOnline
    {
        private readonly IBasicCommunication _coms;
        private readonly bool _comsIsRs232;
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

        private readonly CrestronQueue<byte[]> _queueRs232;
        private readonly CrestronQueue<string> _queueSimpleIp;
        private string _currentInput;
        private bool _powerIsOn;
        private bool _isCooling;
        private bool _isWarming;
        private readonly long _coolingTimeMs;
        private readonly long _warmingtimeMs;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="config"></param>
        /// <param name="comms"></param>
        public SonyBraviaDevice(DeviceConfig config, IBasicCommunication comms)
            : base(config.Key, config.Name)
        {
            DebugLevels.Key = Key;

            var props = config.Properties.ToObject<SonyBraviaConfig>();
            _coolingTimeMs = props.CoolingTimeMs ?? 20000;
            _warmingtimeMs = props.WarmingTimeMs ?? 20000;

            IQueueMessage powerQuery;
            IQueueMessage inputQuery;

            _coms = comms;
            var socket = _coms as ISocketStatus;
            _comsIsRs232 = socket == null;
            if (_comsIsRs232)
            {
                _queueRs232 = new CrestronQueue<byte[]>(50);
                _coms.BytesReceived += (sender, args) => _queueRs232.Enqueue(args.Bytes);

                _powerOnCommand = Rs232Commands.GetPowerOn(_coms);
                _powerOffCommand = Rs232Commands.GetPowerOff(_coms);
                powerQuery = Rs232Commands.GetPowerQuery(_coms);
                inputQuery = Rs232Commands.GetInputQuery(_coms);
            }
            else
            {
                _queueSimpleIp = new CrestronQueue<string>(50);
                var comsGather = new CommunicationGather(_coms, "\x0A");
                comsGather.LineReceived += (sender, args) => _queueSimpleIp.Enqueue(args.Text);

                _powerOnCommand = SimpleIpCommands.GetControlCommand(_coms, "POWR", 1);
                _powerOffCommand = SimpleIpCommands.GetControlCommand(_coms, "POWR", 0);
                powerQuery = SimpleIpCommands.GetQueryCommand(_coms, "POWR");
                inputQuery = SimpleIpCommands.GetQueryCommand(_coms, "INPT");
            }

            if (CommandQueue == null)
                CommandQueue = new GenericQueue(string.Format("{0}-commandQueue", config.Key), 50);

            var monitorConfig = props.CommunicationMonitorProperties ?? DefaultMonitorConfig;
            CommunicationMonitor = new GenericCommunicationMonitor(
                this, _coms, monitorConfig.PollInterval, monitorConfig.TimeToWarning, monitorConfig.TimeToError,
                PowerPoll);

            BuildInputRoutingPorts();

            var worker = _comsIsRs232
                ? new Thread(ProcessRs232Response, null)
                : new Thread(ProcessSimpleIpResponse, null);

            _pollTimer = new CTimer(Poll, new[] { powerQuery, inputQuery }, Timeout.Infinite);

            CrestronEnvironment.ProgramStatusEventHandler += type =>
            {
                try
                {
                    if (type != eProgramStatusEventType.Stopping)
                        return;

                    worker.Abort();

                    _pollTimer.Stop();
                    _pollTimer.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Notice, "Caught an exception at program stop: {0}{1}",
                        ex.Message, ex.StackTrace);
                }
            };

            DeviceManager.AllDevicesActivated += (sender, args) =>
            {
                try
                {
                    _coms.Connect();
                    CommunicationMonitor.Start();
                    _pollTimer.Reset(5000, 15000);
                }
                catch (Exception ex)
                {
                    Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Notice, "Caught an exception at AllDevicesActivated: {0}{1}",
                        ex.Message, ex.StackTrace);
                }
            };
        }

        /// <summary>
        /// Device power is on
        /// </summary>
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

        /// <summary>
        /// Device is cooling
        /// </summary>
        public bool IsCooling
        {
            get { return _isCooling; }
            set
            {
                _isCooling = value;
                IsCoolingDownFeedback.FireUpdate();
            }
        }

        /// <summary>
        /// Device is cooling
        /// </summary>
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

        /// <summary>
        /// Link to API
        /// </summary>
        /// <param name="trilist"></param>
        /// <param name="joinStart"></param>
        /// <param name="joinMapKey"></param>
        /// <param name="bridge"></param>
        public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            LinkDisplayToApi(this, trilist, joinStart, joinMapKey, bridge);
        }

        public StatusMonitorBase CommunicationMonitor { get; private set; }

        public BoolFeedback IsOnline { get { return CommunicationMonitor.IsOnlineFeedback; } }

        /// <summary>
        /// Poll device
        /// </summary>
        /// <param name="o"></param>
        public static void Poll(object o)
        {
            var commands = o as IEnumerable<IQueueMessage>;
            if (commands == null)
                return;

            commands.ToList().ForEach(CommandQueue.Enqueue);
        }

        /// <summary>
        /// Turn device power on
        /// </summary>
        public override void PowerOn()
        {
            CommandQueue.Enqueue(_powerOnCommand);
            _pollTimer.Reset(1000, 15000);
        }

        /// <summary>
        /// Turn device power off
        /// </summary>
        public override void PowerOff()
        {
            CommandQueue.Enqueue(_powerOffCommand);
            _pollTimer.Reset(1000, 15000);
        }

        /// <summary>
        /// Toggle device power
        /// </summary>
        public override void PowerToggle()
        {
            if (PowerIsOn)
            {
                PowerOff();
            }
            else
            {
                PowerOn();
            }
        }

        /// <summary>
        /// Poll device for power state
        /// </summary>
        public void PowerPoll()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetPowerQuery(_coms)
                : SimpleIpCommands.GetQueryCommand(_coms, "POWR"));
        }

        /// <summary>
        /// Print a list of input routing ports
        /// </summary>
        public void ListRoutingInputPorts()
        {
            var seperator = new string('*', 50);

            Debug.Console(DebugLevels.TraceLevel, this, seperator);
            foreach (var inputPort in InputPorts)
            {
                Debug.Console(DebugLevels.TraceLevel, this, "inputPort key: {0}, connectionType: {1}, feedbackMatchObject: {2}, port: {3}",
                    inputPort.Key, inputPort.ConnectionType, inputPort.FeedbackMatchObject, inputPort.Port);
            }
            Debug.Console(DebugLevels.TraceLevel, this, seperator);
        }

        private void AddInputRoutingPort(RoutingInputPort input, int port)
        {
            input.Port = port;
            InputPorts.Add(input);
        }

        /// <summary>
        /// Build input routing ports
        /// </summary>
        public void BuildInputRoutingPorts()
        {
            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn1, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi1), this), 1);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn2, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi2), this), 2);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn3, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi3), this), 3);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn4, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi4), this), 4);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn5, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi5), this), 5);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.VgaIn1, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Vga,
                    new Action(InputVga1), this), 6);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.CompositeIn, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo1), this), 7);

            AddInputRoutingPort(new RoutingInputPort(
                    "CompositeIn2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo2), this), 8);

            AddInputRoutingPort(new RoutingInputPort(
                    "CompositeIn2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo3), this), 9);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.ComponentIn, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component,
                    new Action(InputVideo3), this), 10);

            AddInputRoutingPort(new RoutingInputPort(
                    "componentIn2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component,
                    new Action(InputComponent2), this), 11);

            AddInputRoutingPort(new RoutingInputPort(
                    "componentIn3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component,
                    new Action(InputComponent3), this), 12);
        }

        /// <summary>
        /// Select HDMI 1 input
        /// </summary>
        public void InputHdmi1()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetHdmi1(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 1));
        }

        /// <summary>
        /// Select HDMI 2 input
        /// </summary>
        public void InputHdmi2()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetHdmi2(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 2));
        }

        /// <summary>
        /// Select HDMI 3 input
        /// </summary>
        public void InputHdmi3()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetHdmi3(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 3));
        }

        /// <summary>
        /// Select HDMI 4 input
        /// </summary>
        public void InputHdmi4()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetHdmi4(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 4));
        }

        /// <summary>
        /// Select HDMI 5 input
        /// </summary>
        public void InputHdmi5()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetHdmi5(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 5));
        }

        /// <summary>
        /// Select Video 1 input
        /// </summary>
        public void InputVideo1()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetVideo1(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 1));
        }

        /// <summary>
        /// Select Video 2 input
        /// </summary>
        public void InputVideo2()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetVideo2(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 2));
        }

        /// <summary>
        /// Select Video 3 input
        /// </summary>
        public void InputVideo3()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetVideo3(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 3));
        }

        /// <summary>
        /// Select Component 1 input
        /// </summary>
        public void InputComponent1()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetComponent1(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 1));
        }

        /// <summary>
        /// Select Component 2 input
        /// </summary>
        public void InputComponent2()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetComponent2(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 2));
        }

        /// <summary>
        /// Select Component 3 input
        /// </summary>
        public void InputComponent3()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetComponent3(_coms)
                : SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 3));
        }

        /// <summary>
        /// Select PC input using the IInputVga1 interface
        /// </summary>
        public void InputVga1()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetComponent1(_coms)
                : null);
        }

        /// <summary>
        /// Poll device for input state
        /// </summary>
        public void InputPoll()
        {
            // byte[] poll = { 0x83, 0x00, 0x02, 0xFF, 0xFF, 0x83 };
            //CommandQueue.Enqueue(Rs232Commands.GetInputQuery(_coms));
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetInputQuery(_coms)
                : SimpleIpCommands.GetQueryCommand(_coms, "INPT"));
        }

        /// <summary>
        /// Execute switch
        /// </summary>
        /// <param name="selector"></param>
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

        private object ProcessRs232Response(object _)
        {
            var seperator = new string('-', 50);

            byte[] buffer = null;
            while (true)
            {
                try
                {
                    var bytes = _queueRs232.Dequeue();
                    if (bytes == null)
                    {
                        Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: _queueRs232.Dequeue failed, object was null");
                        return null;
                    }

                    Debug.Console(DebugLevels.ErrorLevel, this, seperator);
                    Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: bytes-'{0}' (len-'{1}')",
                        bytes.ToReadableString(), bytes.Length);

                    if (buffer == null)
                        buffer = bytes;
                    else
                    {
                        var newBuffer = new byte[buffer.Length + bytes.Length];
                        buffer.CopyTo(newBuffer, 0);
                        bytes.CopyTo(newBuffer, buffer.Length);
                        buffer = newBuffer;
                    }

                    Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: bytes-'{0}' (len-'{1}') | buffer-'{2}' (len-'{3}')",
                        bytes.ToReadableString(), bytes.Length, buffer.ToReadableString(), buffer.Length);

                    if (!buffer.ContainsHeader())
                    {
                        Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: buffer-'{0}' (len-'{1}') did not contain a header",
                            buffer.ToReadableString(), buffer.Length);

                        continue;
                    }

                    if (buffer.ElementAtOrDefault(0) != 0x70)
                        buffer = buffer.CleanToFirstHeader();

                    Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: bytes-'{0}' (len-'{1}') | buffer-'{2}' (len-'{3}')",
                        bytes.ToReadableString(), bytes.Length, buffer.ToReadableString(), buffer.Length);

                    while (buffer.Length >= 3)
                    {
                        var message = buffer.GetFirstMessage();
                        Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: bytes-'{0}' (len-'{1}') | buffer-'{2}' (len-'{3}') | message-'{4}' (len-'{5}')",
                            bytes.ToReadableString(), bytes.Length,
                            buffer.ToReadableString(), buffer.Length,
                            message.ToReadableString(), message.Length);

                        if (message.Length < 4)
                        {
                            // we have an ACK in here, let's print it out and keep moving                            
                            switch (message.ToReadableString())
                            {
                                // response to query request (abnormal end) - Command Cancelled
                                // package is recieved normally, but the request is not acceptable in the current display status
                                case "07-00-70":
                                    {
                                        Debug.Console(DebugLevels.DebugLevel, this, "Control complete ({0})", message.ToReadableString());
                                        break;
                                    }
                                case "07-01-71":
                                    {
                                        Debug.Console(DebugLevels.DebugLevel, this, "Abnormal End: over maximum value ({0})", message.ToReadableString());
                                        break;
                                    }
                                case "07-02-72":
                                    {
                                        Debug.Console(DebugLevels.DebugLevel, this, "Abnormal End: under minimum value ({0})", message.ToReadableString());
                                        break;
                                    }
                                case "70-03-73":
                                    {
                                        Debug.Console(DebugLevels.DebugLevel, this, "Abnormal End: command cancelled ({0})", message.ToReadableString());
                                        break;
                                    }
                                case "70-04-74":
                                    {
                                        Debug.Console(DebugLevels.DebugLevel, this, "Abnormal End: parse error/data format error ({0})", message.ToReadableString());
                                        break;
                                    }
                                default:
                                    {
                                        Debug.Console(DebugLevels.DebugLevel, this, "Unknown Response: {0}", message.ToReadableString());
                                        break;
                                    }
                            }

                            buffer = buffer.CleanOutFirstMessage();
                            Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: buffer-'{0}' (len-'{1}')",
                                buffer.ToReadableString(), buffer.Length);

                            continue;
                        }

                        // we have a full message, lets check it out
                        Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: message-'{0}' (len-'{1}')",
                            message.ToReadableString(), message.Length);

                        var dataSize = message[2];
                        var totalDataSize = dataSize + 3;
                        var isComplete = totalDataSize == message.Length;
                        Debug.Console(
                            DebugLevels.ErrorLevel, this, "ProcessRs232Response: dataSize-'{0}' | totalDataSize-'{1}' | message.Length-'{2}'",
                            dataSize, totalDataSize, message.Length);

                        if (!isComplete)
                        {
                            Debug.Console(DebugLevels.DebugLevel, this, "Message is incomplete... spinning around");
                            continue;
                        }

                        bool powerResult;
                        if (buffer.ParsePowerResponse(out powerResult))
                        {
                            PowerIsOn = powerResult;
                            Debug.Console(DebugLevels.DebugLevel, "PowerIsOn: {0}", PowerIsOn.ToString());
                        }

                        string input;
                        if (buffer.ParseInputResponse(out input))
                        {
                            _currentInput = input;
                            CurrentInputFeedback.FireUpdate();
                        }

                        buffer = buffer.NumberOfHeaders() > 1 ? buffer.CleanOutFirstMessage() : new byte[0];

                        Debug.Console(DebugLevels.DebugLevel, this, seperator);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Console(DebugLevels.TraceLevel, this, Debug.ErrorLogLevel.Error, "ProcessRs232Response Exception: {0}", ex.Message);
                    Debug.Console(DebugLevels.DebugLevel, this, Debug.ErrorLogLevel.Error, "ProcessRs232Response Exception Stack Trace: {0}", ex.StackTrace);
                    if (ex.InnerException != null)
                        Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Error, "ProcessRs232Response Inner Exception: {0}", ex.InnerException);

                    Debug.Console(DebugLevels.DebugLevel, this, seperator);
                }
            }
        }

        private object ProcessSimpleIpResponse(object _)
        {
            var seperator = new string('-', 50);

            while (true)
            {
                try
                {
                    var response = _queueSimpleIp.Dequeue();
                    if (response == null)
                    {
                        Debug.Console(DebugLevels.ErrorLevel, this, "ProcessSimpleIpResponse: _queueSimpleIp.Dequeue failed, object was null");
                        return null;
                    }

                    Debug.Console(DebugLevels.ErrorLevel, this, seperator);
                    Debug.Console(DebugLevels.ErrorLevel, this, "ProcessSimpleIpResponse: raw '{0}'", response);

                    // http://regexstorm.net/tester
                    // *([A,C,E,N])(?<command>POWR|INPT|VOLU|AMUT)(?<parameters>.[Ff]+|\d+)
                    // *(?<type>[A,C,E,N]{1})(?<command>[A-Za-z]{4})(?<parameters>.\w+)
                    // - CPOWR0000000000000000\n
                    // - AINPT0000000000000001\n                    
                    // - CVOLU0000000000000001\n
                    // - AAMUTFFFFFFFFFFFFFFFF\n
                    var expression = new Regex(@"(?<type>[A,C,E,N]{1})(?<command>[A-Za-z]{4})(?<parameters>.\w+)", RegexOptions.None);
                    var matches = expression.Match(response);

                    if (!matches.Success)
                    {
                        Debug.Console(DebugLevels.TraceLevel, this, "ProcessSimpleIpResponse: unknown response '{0}'", response);
                        return null;
                    }

                    var type = matches.Groups["type"].Value;
                    var command = matches.Groups["command"].Value;
                    var parameters = matches.Groups["parameters"].Value;
                    Debug.Console(DebugLevels.ErrorLevel, this, "ProcessSimpleIpResponse: type-'{0}' | command-'{1}' | parameters-'{2}'",
                        type, command, parameters);

                    // display off input response: 
                    // - '*SAINPTFFFFFFFFFFFFFFFF'
                    // - '*SAINPTNNNNNNNNNNNNNNNN'
                    if (parameters.Contains('F') || parameters.Contains('N')) continue;

                    switch (command)
                    {
                        case "POWR":
                            {
                                PowerIsOn = Convert.ToInt16(parameters) == 1;
                                Debug.Console(DebugLevels.ErrorLevel, this, "ProcessSimpleIpResponse: PowerIsOn == '{0}'", PowerIsOn.ToString());
                                break;
                            }
                        case "INPT":
                            {
                                // display on response:
                                // - '*SAINPT0000000100000001' (hdmi 1)
                                // - '*SAINPT0000000400000001' (component 1)
                                var parts = parameters.SplitInParts(8);
                                var inputParts = parts as IList<string> ?? parts.ToList();
                                var inputType = (SimpleIpCommands.InputTypes)Convert.ToInt16(inputParts.ElementAt(0));
                                var inputNumber = Convert.ToInt16(inputParts.ElementAt(1));

                                Debug.Console(DebugLevels.ErrorLevel, this, "ProcessSimpleIpResponse: inputType == '{0}' | inputNumber == '{1}'",
                                    inputType, inputNumber);

                                switch (inputType)
                                {
                                    case SimpleIpCommands.InputTypes.Hdmi:
                                        {
                                            _currentInput = inputNumber.ToString(CultureInfo.InvariantCulture);
                                            break;
                                        }
                                    case SimpleIpCommands.InputTypes.Component:
                                        {
                                            var index = inputNumber + 9;
                                            _currentInput = index.ToString(CultureInfo.InvariantCulture);
                                            break;
                                        }
                                    case SimpleIpCommands.InputTypes.Composite:
                                        {
                                            var index = inputNumber + 6;
                                            _currentInput = index.ToString(CultureInfo.InvariantCulture);
                                            break;
                                        }
                                    default:
                                        {
                                            // unknown input type
                                            break;
                                        }
                                }

                                Debug.Console(DebugLevels.ErrorLevel, this, "ProcessSimpleIpResponse: _currentInput == '{0}'", _currentInput);

                                break;
                            }
                        default:
                            {
                                Debug.Console(DebugLevels.DebugLevel, this, "ProcessSimpleIpResponse: unhandled response '{0}' == '{1}'",
                                    command, parameters);
                                break;
                            }
                    }

                    Debug.Console(DebugLevels.ErrorLevel, this, seperator);
                }
                catch (Exception ex)
                {
                    Debug.Console(DebugLevels.TraceLevel, this, Debug.ErrorLogLevel.Error,
                        "ProcessSimpleIpResponse Exception: {0}", ex.Message);
                    Debug.Console(DebugLevels.DebugLevel, this, Debug.ErrorLogLevel.Error,
                        "ProcessSimpleIpResponse Exception Stack Trace: {0}", ex.StackTrace);
                    if (ex.InnerException != null)
                        Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Error,
                            "ProcessSimpleIpResponse Inner Exception: {0}", ex.InnerException);

                    Debug.Console(DebugLevels.DebugLevel, this, seperator);
                }
            }
        }
    }
}