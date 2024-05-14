﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.DeviceSupport;
using Org.BouncyCastle.Asn1.Cmp;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Queues;
using PepperDash.Essentials.Core.Routing;
using PepperDash.Essentials.Devices.Displays;

namespace SonyBraviaEpi
{
    public class SonyBraviaDevice : TwoWayDisplayBase, ICommunicationMonitor, IBridgeAdvanced,
        IInputHdmi1, IInputHdmi2, IInputHdmi3, IInputHdmi4, IInputVga1,
        IOnline,
        IBasicVolumeWithFeedback
#if SERIES4
        , IHasInputs<string, string>
#endif
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

        private byte[] _incomingBuffer = { };

        private readonly GenericQueue _queueRs232;
        private readonly CrestronQueue<string> _queueSimpleIp;
        private string _currentInput;
        private bool _powerIsOn;
        private bool _isCooling;
        private bool _isWarming;
        private readonly long _coolingTimeMs;
        private readonly long _warmingtimeMs;

        private int pollIndex = 0;

        private Dictionary<byte, string> _ackStringFormats = new Dictionary<byte, string> {
            {0x00, "Control complete ({0})"},
            {0x01, "Abnormal End: over maximum value ({0})" },
            {0x02, "Abnormal End: under minimum value ({0})" },
            {0x03, "Abnormal End: command cancelled ({0})"},
            {0x04, "Abnormal End: parse error/data format error ({0})" }
        };      
    
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
            IQueueMessage volumeQuery;
            IQueueMessage muteQuery;

            _coms = comms;
            var socket = _coms as ISocketStatus;
            _comsIsRs232 = socket == null || props.ForceRs232;
            if (_comsIsRs232)
            {
                _queueRs232 = new GenericQueue(string.Format("{0}-r232queue", Key), 50);                

                _coms.BytesReceived += (sender, args) => {
                    Debug.Console(DebugLevels.DebugLevel, this, "received response: {0}", ComTextHelper.GetEscapedText(args.Bytes));
                    _queueRs232.Enqueue(new Rs232Response(args.Bytes, ProcessRs232Response));
                };

                //_coms.BytesReceived += (sender, args) => ProcessRs232Response(args.Bytes);


                _powerOnCommand = Rs232Commands.GetPowerOn(_coms, UpdateLastSentCommandType);
                _powerOffCommand = Rs232Commands.GetPowerOff(_coms, UpdateLastSentCommandType);
                powerQuery = Rs232Commands.GetPowerQuery(_coms, UpdateLastSentCommandType);
                inputQuery = Rs232Commands.GetInputQuery(_coms, UpdateLastSentCommandType);
                volumeQuery = Rs232Commands.GetVolumeQuery(_coms, UpdateLastSentCommandType);
                muteQuery = Rs232Commands.GetMuteQuery(_coms, UpdateLastSentCommandType);
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
                volumeQuery = SimpleIpCommands.GetQueryCommand(_coms, "VOLU");
                muteQuery = SimpleIpCommands.GetQueryCommand(_coms, "AMUT");
            }

            if (CommandQueue == null)
                CommandQueue = new GenericQueue(string.Format("{0}-commandQueue", config.Key),500, 50);

            var monitorConfig = props.CommunicationMonitorProperties ?? DefaultMonitorConfig;
            CommunicationMonitor = new GenericCommunicationMonitor(
                this, _coms, monitorConfig.PollInterval, monitorConfig.TimeToWarning, monitorConfig.TimeToError,
                PowerPoll);

            BuildInputRoutingPorts();

            SetupInputs();


            var worker = _comsIsRs232
                ? null //new Thread(ProcessRs232Response, null)
                : new Thread(ProcessSimpleIpResponse, null);

            _pollTimer = _comsIsRs232
                ? new CTimer((o) => PollRs232(new List<byte[]> { Rs232Commands.PowerQuery.WithChecksum(), Rs232Commands.InputQuery.WithChecksum(), Rs232Commands.VolumeQuery.WithChecksum(), Rs232Commands.MuteQuery.WithChecksum()}),Timeout.Infinite) 
                : new CTimer((o) => Poll(new List<IQueueMessage> { powerQuery, inputQuery, muteQuery, volumeQuery }),Timeout.Infinite);

            MuteFeedback = new BoolFeedback(() => _muted);
            VolumeLevelFeedback = new IntFeedback(() => CrestronEnvironment.ScaleWithLimits(_rawVolume, 255, 0, 65535, 0));

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
        }

        public override void Initialize()
        {
            try
            {
                _coms.Connect();
                CommunicationMonitor.Start();
                _pollTimer.Reset(0, 1000);
            }
            catch (Exception ex)
            {
                Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Notice, "Caught an exception at AllDevicesActivated: {0}{1}",
                    ex.Message, ex.StackTrace);
            }
        }

        private void PollRs232(List<byte[]> pollCommands)
        {            
            if (pollIndex >= pollCommands.Count)
            {
                pollIndex = 0;
            }

            var command = pollCommands[pollIndex];

            Debug.Console(2, this, "Sending command {0}", ComTextHelper.GetEscapedText(command));

            _lastCommand = command;
            _coms.SendBytes(command);

            pollIndex += 1;            
        }

        private byte[] _lastCommand;

        private eCommandType _lastCommandType;

        private void UpdateLastSentCommandType(eCommandType commandType)
        {
            Debug.Console(DebugLevels.TraceLevel, this, "Setting last command type to {0}", commandType);
            _lastCommandType = commandType;
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

#if SERIES4
        public ISelectableItems<string> Inputs { get; private set; }
#endif
        private bool _muted;

        private int _rawVolume;

        public BoolFeedback MuteFeedback { get; private set; }

        public IntFeedback VolumeLevelFeedback { get; private set; }

        /// <summary>
        /// Poll device
        /// </summary>
        /// <param name="o"></param>
        public static void Poll(List<IQueueMessage> commands)
        {
            foreach(var command in commands)
            {
                CommandQueue.Enqueue(command);
            }
        }

        /// <summary>
        /// Turn device power on
        /// </summary>
        public override void PowerOn()
        {
            if (_comsIsRs232) {
                var command = Rs232Commands.PowerOn.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                return;
            }
            CommandQueue.Enqueue(_powerOnCommand);
            _pollTimer.Reset(1000, 1000);
        }

        /// <summary>
        /// Turn device power off
        /// </summary>
        public override void PowerOff()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.PowerOff.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                return;
            }
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
                ? Rs232Commands.GetPowerQuery(_coms, UpdateLastSentCommandType)
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

        private void SetupInputs()
        {
#if SERIES4
            Inputs = new SonyBraviaInputs
            {
                Items = new Dictionary<string, ISelectableItem>
                {
                    {
                        "hdmi1", _comsIsRs232
                            ? new SonyBraviaInput("Hdmi1", "HDMI 1", _coms, Rs232Commands.InputHdmi1.WithChecksum())
                            : new SonyBraviaInput("Hdmi1", "HDMI 1", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 1))
                    },
                    {

                        "hdmi2", _comsIsRs232
                            ? new SonyBraviaInput("Hdmi2", "HDMI 2", _coms, Rs232Commands.InputHdmi2.WithChecksum())
                            : new SonyBraviaInput("Hdmi2", "HDMI 2", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 2))
                    },
                    {
                        "hdmi3", _comsIsRs232
                            ? new SonyBraviaInput("Hdmi3", "HDMI 3", _coms, Rs232Commands.InputHdmi3.WithChecksum())
                            : new SonyBraviaInput("Hdmi3", "HDMI 3", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 3))
                    },
                    {
                        "hdmi4", _comsIsRs232
                            ? new SonyBraviaInput("Hdmi4", "HDMI 4", _coms, Rs232Commands.InputHdmi4.WithChecksum())
                            : new SonyBraviaInput("Hdmi4", "HDMI 4", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 4))
                    },
                    {
                        "hdmi5", _comsIsRs232
                            ? new SonyBraviaInput("Hdmi5", "HDMI 5", _coms, Rs232Commands.InputHdmi5.WithChecksum())
                            : new SonyBraviaInput("Hdmi5", "HDMI 5", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 5))
                    },
                    {
                        "video1",_comsIsRs232 
                            ? new SonyBraviaInput("video1", "Video 1", _coms, Rs232Commands.InputVideo1.WithChecksum())
                            : new SonyBraviaInput("video1", "Video 1", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 1))
                    },
                    {
                        "video2",_comsIsRs232
                            ? new SonyBraviaInput("video2", "Video 2", _coms, Rs232Commands.InputVideo2.WithChecksum())
                            : new SonyBraviaInput("video2", "Video 2", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 2))
                    },
                    {
                        "video3", _comsIsRs232
                            ? new SonyBraviaInput("video3", "Video 3", _coms, Rs232Commands.InputVideo3.WithChecksum())
                            : new SonyBraviaInput("video3", "Video 3", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 3))
                    },
                    {
                        "component1", _comsIsRs232
                            ?  new SonyBraviaInput("component1", "Component 1", _coms, Rs232Commands.InputComponent1.WithChecksum())
                            : new SonyBraviaInput("component1", "Component 1", this,SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 1))
                    },
                    {
                        "component2", _comsIsRs232
                            ?  new SonyBraviaInput("component2", "Component 2", _coms, Rs232Commands.InputComponent2.WithChecksum())
                            : new SonyBraviaInput("component2", "Component 2", this,SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 2))
                    },
                    {
                        "component3", _comsIsRs232
                            ?  new SonyBraviaInput("component3", "Component 3", _coms, Rs232Commands.InputComponent3.WithChecksum())
                            : new SonyBraviaInput("component3", "Component 3", this,SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 3))
                    },
                    {
                        "vga1",_comsIsRs232 ? new SonyBraviaInput("vga1", "VGA 1", _coms, Rs232Commands.InputComponent1.WithChecksum())
                        : new SonyBraviaInput("vga1", "VGA 1", this, null)
                    }
                }
            };
#endif
        }

        /// <summary>
        /// Select HDMI 1 input
        /// </summary>
        public void InputHdmi1()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputHdmi1.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 1));
        }

        /// <summary>
        /// Select HDMI 2 input
        /// </summary>
        public void InputHdmi2()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputHdmi2.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 2));
        }

        /// <summary>
        /// Select HDMI 3 input
        /// </summary>
        public void InputHdmi3()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputHdmi3.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 3));
        }

        /// <summary>
        /// Select HDMI 4 input
        /// </summary>
        public void InputHdmi4()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputHdmi4.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 4));
        }

        /// <summary>
        /// Select HDMI 5 input
        /// </summary>
        public void InputHdmi5()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputHdmi5.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 5));
        }

        /// <summary>
        /// Select Video 1 input
        /// </summary>
        public void InputVideo1()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputVideo1.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 1));
        }

        /// <summary>
        /// Select Video 2 input
        /// </summary>
        public void InputVideo2()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputVideo2.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 2));
        }

        /// <summary>
        /// Select Video 3 input
        /// </summary>
        public void InputVideo3()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputVideo3.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 3));
        }

        /// <summary>
        /// Select Component 1 input
        /// </summary>
        public void InputComponent1()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputComponent1.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 1));
        }

        /// <summary>
        /// Select Component 2 input
        /// </summary>
        public void InputComponent2()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputComponent2.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 2));
        }

        /// <summary>
        /// Select Component 3 input
        /// </summary>
        public void InputComponent3()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.InputComponent3.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 3));
        }

        /// <summary>
        /// Select PC input using the IInputVga1 interface
        /// </summary>
        public void InputVga1()
        {
            if (!_comsIsRs232) return;

            var command = Rs232Commands.InputComponent1.WithChecksum();
            _coms.SendBytes(command);
            _lastCommand = command;
            return;
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

        private void ProcessRs232Response(byte[] response)
        {
            try
            {
                var buffer = new byte[_incomingBuffer.Length + response.Length];
                _incomingBuffer.CopyTo(buffer, 0);
                response.CopyTo(buffer, _incomingBuffer.Length);
                
                Debug.Console(DebugLevels.ErrorLevel, this, "ProcessRs232Response: {0}", ComTextHelper.GetEscapedText(buffer));
                
                //it starts a valid response
                if (buffer.Length >= 3)
                {
                    //If the message is an ack, byte3 will be the sum of the first 2 bytes
                    if (buffer[0] == 0x70 & buffer[2] == buffer[0] + buffer[1])
                    {
                        var message = new byte[3];
                        buffer.CopyTo(message, 0);
                        HandleAck(message);
                        byte[] clear = { };
                        _incomingBuffer = clear;
                        return;
                    }

                    //header length is 3.
                    var messageLength = 3 + buffer[2];

                    if(buffer[0] == 0x70 && buffer.Length >= messageLength)
                    {
                        var message = new byte[messageLength];
                        buffer.CopyTo(message, 0);
                        ParseMessage(message);
                        byte[] clear = { };
                        _incomingBuffer = clear;
                        return;
                    }
                }

                if (buffer[0] == 0x70)
                {
                    _incomingBuffer = buffer;
                } else
                {
                    byte[] clear = { };
                    _incomingBuffer = clear;
                }
                        
            }
            catch (Exception ex)
            {
                Debug.Console(DebugLevels.TraceLevel, this, Debug.ErrorLogLevel.Error, "ProcessRs232Response Exception: {0}", ex.Message);
                Debug.Console(DebugLevels.DebugLevel, this, Debug.ErrorLogLevel.Error, "ProcessRs232Response Exception Stack Trace: {0}", ex.StackTrace);
                if (ex.InnerException != null)
                    Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Error, "ProcessRs232Response Inner Exception: {0}", ex.InnerException);                
            }            
        }

        private void ParseMessage(byte[] message)
        {
            // 3rd byte is the command type
            switch (_lastCommand[2])
            {
                case 0x00: //power
                    PowerIsOn = Rs232ParsingUtils.ParsePowerResponse(message);
                    PowerIsOnFeedback.FireUpdate(); 
                    break;
                case 0x02: //input
                    _currentInput = Rs232ParsingUtils.ParseInputResponse(message);
                    CurrentInputFeedback.FireUpdate();
#if SERIES4
                    if (Inputs.Items.ContainsKey(_currentInput))
                    {
                        foreach(var input in Inputs.Items)
                        {
                            input.Value.IsSelected = input.Key.Equals(_currentInput);
                        }
                    }

                    Inputs.CurrentItem = _currentInput;
#endif


                    break;
                case 0x05: //volume
                    _rawVolume = Rs232ParsingUtils.ParseVolumeResponse(message);
                    VolumeLevelFeedback.FireUpdate();
                    break;
                case 0x06: //mute
                    _muted = Rs232ParsingUtils.ParseMuteResponse(message);
                    MuteFeedback.FireUpdate();
                    break;
                default:
                    Debug.Console(0, this, "Unknown response received: {0}", ComTextHelper.GetEscapedText(message));
                    break;
            }
        }

        private void HandleAck(byte[] message)
        {
            string consoleMessageFormat;

            if (!_ackStringFormats.TryGetValue(message[1], out consoleMessageFormat))
            {
                Debug.Console(DebugLevels.DebugLevel, this, "Unknown Response: {0}", ComTextHelper.GetEscapedText(message));
                return;
            }

            Debug.Console(DebugLevels.DebugLevel, this, consoleMessageFormat, ComTextHelper.GetEscapedText(message));
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


#if SERIES4
                                // No idea if this will work with _currentInput.  It's not clear how the input is determined for IP communication
                                if (Inputs.Items.ContainsKey(_currentInput))
                                {
                                    foreach (var item in Inputs.Items)
                                    {
                                        item.Value.IsSelected = item.Key.Equals(_currentInput);
                                    }
                                }

                                Inputs.CurrentItem = _currentInput;
#endif
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

        public void EnqueueCommand(IQueueMessage command)
        {
            CommandQueue.Enqueue(command);
        }

#if SERIES4
        public void SetInput(string selector)
        {
            var input = Inputs.Items[selector];

            if (input != null)
            {
                input.Select();
            }

            _pollTimer.Reset(1000, 15000);
        }
#endif
        public void MuteOn()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetMuteOn(_coms, UpdateLastSentCommandType)
                : null);
            _pollTimer.Reset(1000, 15000);
        }

        public void MuteOff()
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetMuteOff(_coms, UpdateLastSentCommandType)
                : null);
            _pollTimer.Reset(1000, 15000);
        }
        public void MuteToggle()
        {
            if (_muted)
            {
                MuteOff();
                return;
            }

            MuteOn();
        }

        public void SetVolume(ushort level)
        {
            var scaledVolume = CrestronEnvironment.ScaleWithLimits(level, 65535, 0, 255, 0);

            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetVolumeDirect(_coms, UpdateLastSentCommandType, scaledVolume)
                : null);
            _pollTimer.Reset(1000, 15000);
        }

        public void VolumeUp(bool pressRelease)
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetVolumeUp(_coms, UpdateLastSentCommandType)
                : null);
            _pollTimer.Reset(1000, 15000);
        }

        public void VolumeDown(bool pressRelease)
        {
            CommandQueue.Enqueue(_comsIsRs232
                ? Rs232Commands.GetVolumeDown(_coms, UpdateLastSentCommandType)
                : null);
            _pollTimer.Reset(1000, 15000);
        }


    }
}