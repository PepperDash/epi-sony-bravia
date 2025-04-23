using Crestron.SimplSharp;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Queues;
using PepperDash.Essentials.Devices.Displays;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TwoWayDisplayBase = PepperDash.Essentials.Devices.Common.Displays.TwoWayDisplayBase;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaDevice : TwoWayDisplayBase, ICommunicationMonitor, IBridgeAdvanced,
        IInputHdmi1, IInputHdmi2, IInputHdmi3, IInputHdmi4, IInputVga1,
        IOnline,
        IBasicVolumeWithFeedbackAdvanced,
        IHasPowerControlWithFeedback,
        IRoutingSinkWithSwitchingWithInputPort,
        IHasInputs<string>
    {
        private const long pollTime = 2000;
        private readonly IBasicCommunication _coms;
        private readonly bool _comsIsRs232;
        public bool ComsIsRs232 { get { return _comsIsRs232; } }
        public static GenericQueue CommandQueue;

        public static readonly CommunicationMonitorConfig DefaultMonitorConfig = new CommunicationMonitorConfig
        {
            PollInterval = 30000,
            TimeToWarning = 60000,
            TimeToError = 120000
        };

        private readonly CTimer _pollTimer;
        private CTimer _volumeTimer;
        private int _volumeCounter;
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
        private List<SonyBraviaInputConfig> _activeInputs;
        public List<SonyBraviaInputConfig> AvailablePictureModes { get; private set; }

        private Dictionary<byte, string> _ackStringFormats = new Dictionary<byte, string> {
            {0x00, "Control complete ({0})"},
            {0x01, "Abnormal End: over maximum value ({0})" },
            {0x02, "Abnormal End: under minimum value ({0})" },
            {0x03, "Abnormal End: command cancelled ({0})"},
            {0x04, "Abnormal End: parse error/data format error ({0})" }
        };

        private byte maxVolumeLevel = 0xFF;
        private Dictionary<string, ISelectableItem> _defaultInputs;


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

            this.LogInformation("Cooling time: {coolingTimeMs} Warming time: {warmingTimeMs}", _coolingTimeMs, _warmingtimeMs);
            this.LogInformation("Config Cooling time: {coolingTimeMs} Warming time: {warmingTimeMs}", props.CoolingTimeMs, props.WarmingTimeMs);

            IQueueMessage powerQuery;
            IQueueMessage inputQuery;
            IQueueMessage volumeQuery;
            IQueueMessage muteQuery;

            _coms = comms;
            _comsIsRs232 = !(_coms is ISocketStatus socket) || props.ForceRs232;
            if (_comsIsRs232)
            {
                _queueRs232 = new GenericQueue(string.Format("{0}-r232queue", Key), 50);

                _coms.BytesReceived += (sender, args) =>
                {
                    Debug.Console(DebugLevels.DebugLevel, this, "received response: {0}", ComTextHelper.GetEscapedText(args.Bytes));

                    _queueRs232.Enqueue(new Rs232Response(args.Bytes, ProcessRs232Response));
                };

                _powerOnCommand = Rs232Commands.GetPowerOn(_coms, (c) => { });
                _powerOffCommand = Rs232Commands.GetPowerOff(_coms, (c) => { });
                powerQuery = Rs232Commands.GetPowerQuery(_coms, (c) => { });
                inputQuery = Rs232Commands.GetInputQuery(_coms, (c) => { });
                volumeQuery = Rs232Commands.GetVolumeQuery(_coms, (c) => { });
                muteQuery = Rs232Commands.GetMuteQuery(_coms, (c) => { });
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
                CommandQueue = new GenericQueue(string.Format("{0}-commandQueue", config.Key), 500, 50);

            var monitorConfig = props.CommunicationMonitorProperties ?? DefaultMonitorConfig;
            CommunicationMonitor = new GenericCommunicationMonitor(
                this, _coms, monitorConfig.PollInterval, monitorConfig.TimeToWarning, monitorConfig.TimeToError,
                PowerPoll);

            BuildInputRoutingPorts();

            _activeInputs = props.ActiveInputs;

            this.LogVerbose("active inputs from config {@activeInputs}", _activeInputs);

            var empty = new byte[] { };

            _defaultInputs = new Dictionary<string, ISelectableItem>
            {
                {
                    "hdmi1", _comsIsRs232
                        ? new SonyBraviaInput("Hdmi1", "HDMI 1", this, Rs232Commands.InputHdmi1.WithChecksum())
                        : new SonyBraviaInput("Hdmi1", "HDMI 1", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 1))
                },
                {

                    "hdmi2", _comsIsRs232
                        ? new SonyBraviaInput("Hdmi2", "HDMI 2", this, Rs232Commands.InputHdmi2.WithChecksum())
                        : new SonyBraviaInput("Hdmi2", "HDMI 2", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 2))
                },
                {
                    "hdmi3", _comsIsRs232
                        ? new SonyBraviaInput("Hdmi3", "HDMI 3", this, Rs232Commands.InputHdmi3.WithChecksum())
                        : new SonyBraviaInput("Hdmi3", "HDMI 3", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 3))
                },
                {
                    "hdmi4", _comsIsRs232
                        ? new SonyBraviaInput("Hdmi4", "HDMI 4", this, Rs232Commands.InputHdmi4.WithChecksum())
                        : new SonyBraviaInput("Hdmi4", "HDMI 4", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 4))
                },
                {
                    "hdmi5", _comsIsRs232
                        ? new SonyBraviaInput("Hdmi5", "HDMI 5", this, Rs232Commands.InputHdmi5.WithChecksum())
                        : new SonyBraviaInput("Hdmi5", "HDMI 5", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 5))
                },
                {
                    "video1",_comsIsRs232
                        ? new SonyBraviaInput("video1", "Video 1", this, Rs232Commands.InputVideo1.WithChecksum())
                        : new SonyBraviaInput("video1", "Video 1", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 1))
                },
                {
                    "video2",_comsIsRs232
                        ? new SonyBraviaInput("video2", "Video 2", this, Rs232Commands.InputVideo2.WithChecksum())
                        : new SonyBraviaInput("video2", "Video 2", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 2))
                },
                {
                    "video3", _comsIsRs232
                        ? new SonyBraviaInput("video3", "Video 3", this, Rs232Commands.InputVideo3.WithChecksum())
                        : new SonyBraviaInput("video3", "Video 3", this, SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 3))
                },
                {
                    "component1", _comsIsRs232
                        ?  new SonyBraviaInput("component1", "Component 1", this, Rs232Commands.InputComponent1.WithChecksum())
                        : new SonyBraviaInput("component1", "Component 1", this,SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 1))
                },
                {
                    "component2", _comsIsRs232
                        ?  new SonyBraviaInput("component2", "Component 2", this, Rs232Commands.InputComponent2.WithChecksum())
                        : new SonyBraviaInput("component2", "Component 2", this,SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 2))
                },
                {
                    "component3", _comsIsRs232
                        ?  new SonyBraviaInput("component3", "Component 3", this, Rs232Commands.InputComponent3.WithChecksum())
                        : new SonyBraviaInput("component3", "Component 3", this,SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 3))
                },
                {
                    "vga1",_comsIsRs232 ? new SonyBraviaInput("vga1", "VGA 1", this, Rs232Commands.InputComponent1.WithChecksum())
                    : new SonyBraviaInput("vga1", "VGA 1", this, empty)
                }
            };

            SetupInputs();


            var worker = _comsIsRs232
                ? null //new Thread(ProcessRs232Response, null)
                : new Thread(ProcessSimpleIpResponse, null);

            _pollTimer = _comsIsRs232
                ? new CTimer((o) => PollRs232(new List<byte[]> { Rs232Commands.PowerQuery.WithChecksum(), Rs232Commands.InputQuery.WithChecksum(), Rs232Commands.VolumeQuery.WithChecksum(), Rs232Commands.MuteQuery.WithChecksum() }), Timeout.Infinite)
                : new CTimer((o) => Poll(new List<IQueueMessage> { powerQuery, inputQuery, muteQuery, volumeQuery }), Timeout.Infinite);

            maxVolumeLevel = props.MaxVolumeLevel;

            MuteFeedback = new BoolFeedback(() => _muted);
            VolumeLevelFeedback = new IntFeedback(() => CrestronEnvironment.ScaleWithLimits(_rawVolume, maxVolumeLevel, 0, 65535, 0));

            PictureModeFeedback = new StringFeedback(() => _pictureMode);
            AvailablePictureModes = props.AvailablePictureModes;    
            
            CrestronEnvironment.ProgramStatusEventHandler += type =>
            {
                try
                {
                    if (type != eProgramStatusEventType.Stopping)
                        return;

                    worker?.Abort();

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
                _pollTimer.Reset(0, pollTime);
            }
            catch (Exception ex)
            {
                Debug.Console(DebugLevels.ErrorLevel, this, Debug.ErrorLogLevel.Notice, "Caught an exception at AllDevicesActivated: {0}{1}",
                    ex.Message, ex.StackTrace);
            }
        }


        public override bool CustomActivate()
        {
            return base.CustomActivate();
        }

        protected override void CreateMobileControlMessengers()
        {
            var mc = DeviceManager.AllDevices.OfType<IMobileControl>().FirstOrDefault();

            if (mc == null)
            {
                this.LogInformation("Mobile Control not found");
                return;
            }

            var messenger = new SonyBraviaPictureModeMessenger($"{Key}-pictureMode", $"/device/{Key}", this);

            mc.AddDeviceMessenger(messenger);
        }

        private void PollRs232(List<byte[]> pollCommands)
        {
            if (pollIndex >= pollCommands.Count)
            {
                pollIndex = 0;
            }

            var command = pollCommands[pollIndex];

            _lastCommand = command;
            _coms.SendBytes(command);

            pollIndex += 1;
        }

        private byte[] _lastCommand;
        public byte[] LastCommand { set { _lastCommand = value; } }

        /// <summary>
        /// Device power is on
        /// </summary>
        public bool PowerIsOn
        {
            get { return _powerIsOn; }
            set
            {
                if (_powerIsOn == value)
                {
                    return;
                }

                if (value)
                {
                    IsWarming = true;

                    WarmupTimer = new CTimer(o =>
                    {
                        _powerIsOn = value;
                        IsWarming = false;
                        PowerIsOnFeedback.FireUpdate();
                    }, _warmingtimeMs);
                }
                else
                {
                    IsCooling = true;

                    CooldownTimer = new CTimer(o =>
                    {
                        _powerIsOn = value;
                        IsCooling = false;
                        PowerIsOnFeedback.FireUpdate();
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

        public int RawVolumeLevel => _rawVolume;

        public eVolumeLevelUnits Units => eVolumeLevelUnits.Absolute;

        /// <summary>
        /// Poll device
        /// </summary>
        /// <param name="o"></param>
        public static void Poll(List<IQueueMessage> commands)
        {
            foreach (var command in commands)
            {
                CommandQueue.Enqueue(command);
            }
        }

        /// <summary>
        /// Turn device power on
        /// </summary>
        public override void PowerOn()
        {
            if (_comsIsRs232)
            {
                _pollTimer.Stop();

                CrestronEnvironment.Sleep(500);

                var command = Rs232Commands.PowerOn.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Reset(500, pollTime);
                return;
            }
            CommandQueue.Enqueue(_powerOnCommand);
            _pollTimer.Reset(1000, pollTime);
        }

        /// <summary>
        /// Turn device power off
        /// </summary>
        public override void PowerOff()
        {
            if (_comsIsRs232)
            {
                _pollTimer.Stop();

                CrestronEnvironment.Sleep(500);

                var command = Rs232Commands.PowerOff.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Reset(500, pollTime);
                return;
            }
            CommandQueue.Enqueue(_powerOffCommand);
            _pollTimer.Reset(1000, pollTime);
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
            if (_comsIsRs232)
            {
                var command = Rs232Commands.PowerQuery.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Reset(1000, pollTime);
                return;
            }
            CommandQueue.Enqueue(SimpleIpCommands.GetQueryCommand(_coms, "POWR"));
            _pollTimer.Reset(1000, pollTime);
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
                    new Action(InputHdmi1), this)
            { FeedbackMatchObject = 0x0401 }, 1);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn2, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi2), this)
            { FeedbackMatchObject = 0x0402 }, 2);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn3, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi3), this)
            { FeedbackMatchObject = 0x0403 }, 3);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn4, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi4), this)
            { FeedbackMatchObject = 0x0404 }, 4);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.HdmiIn5, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(InputHdmi5), this)
            { FeedbackMatchObject = 0x0405 }, 5);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.VgaIn1, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Vga,
                    new Action(InputVga1), this)
            { FeedbackMatchObject = 0x0501 }, 6);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.CompositeIn, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo1), this)
            { FeedbackMatchObject = 0x0201 }, 7);

            AddInputRoutingPort(new RoutingInputPort(
                    "CompositeIn2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo2), this)
            { FeedbackMatchObject = 0x0202 }, 8);

            AddInputRoutingPort(new RoutingInputPort(
                    "CompositeIn3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(InputVideo3), this)
            { FeedbackMatchObject = 0x0203 }, 9);

            AddInputRoutingPort(new RoutingInputPort(
                    RoutingPortNames.ComponentIn, eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component,
                    new Action(InputVideo3), this)
            { FeedbackMatchObject = 0x0301 }, 10);

            AddInputRoutingPort(new RoutingInputPort(
                    "componentIn2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component,
                    new Action(InputComponent2), this)
            { FeedbackMatchObject = 0x0302 }, 11);

            AddInputRoutingPort(new RoutingInputPort(
                    "componentIn3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Component,
                    new Action(InputComponent3), this)
            { FeedbackMatchObject = 0x0303 }, 12);
        }

        private void SetupInputs()
        {
            this.LogDebug("Found {activeInputCount} active Inputs & {defaultInputCount} default Inputs", _activeInputs?.Count, _defaultInputs?.Count);

            Inputs = new SonyBraviaInputs
            {
                Items = _activeInputs == null || _activeInputs.Count == 0
                ? _defaultInputs
                : _defaultInputs
                    .Where(kv => _activeInputs.Any((i) => i.Key.Equals(kv.Key, StringComparison.InvariantCultureIgnoreCase)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            };
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
                if (!(selector is Action action)) return;

                action();


            }
            else
            {
                void handler(object sender, FeedbackEventArgs args)
                {
                    if (IsWarming)
                        return;

                    IsWarmingUpFeedback.OutputChange -= handler;

                    if (!(selector is Action action)) return;

                    action();
                }

                IsWarmingUpFeedback.OutputChange += handler;
                PowerOn();
            }
        }

        public void ResetPolling()
        {
            _pollTimer.Reset(100, pollTime);
        }

        public void SendRs232Command(byte[] command)
        {
            // Debug.LogMessage(Serilog.Events.LogEventLevel.Verbose, "Sending command: {command}",this, command);

            _lastCommand = command;
            _coms.SendBytes(command);
            _pollTimer.Reset(100, pollTime);
        }

        private void ProcessRs232Response(byte[] response)
        {
            try
            {
                var buffer = new byte[_incomingBuffer.Length + response.Length];
                _incomingBuffer.CopyTo(buffer, 0);
                response.CopyTo(buffer, _incomingBuffer.Length);

                // Debug.Console(DebugLevels.DebugLevel, this, "ProcessRs232Response: {0}", ComTextHelper.GetEscapedText(buffer));

                // Debug.LogMessage(Serilog.Events.LogEventLevel.Information, "ProcessRs232Response: {lastCommand}:{response}", this, ComTextHelper.GetEscapedText(_lastCommand), ComTextHelper.GetEscapedText(buffer));

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

                    if (buffer[0] == 0x70 && buffer.Length >= messageLength)
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
                }
                else
                {
                    byte[] clear = { };
                    _incomingBuffer = clear;
                }

            }
            catch (Exception ex)
            {
                Debug.LogMessage(ex, "Caught an exception in ProcessRs232Response: {0}", this, ex.Message);
            }
        }

        private void ParseMessage(byte[] message)
        {
            // 3rd byte is the command type
            switch (_lastCommand[2])
            {
                case 0x00: //power
                    PowerIsOn = message.ParsePowerResponse();
                    PowerIsOnFeedback.FireUpdate();
                    break;
                case 0x02: //input
                    _currentInput = message.ParseInputResponse();
                    CurrentInputFeedback.FireUpdate();

                    if (Inputs.Items.ContainsKey(_currentInput))
                    {
                        foreach (var input in Inputs.Items)
                        {
                            input.Value.IsSelected = input.Key.Equals(_currentInput);
                        }
                    }

                    Inputs.CurrentItem = _currentInput;

                    var inputNumber = message[3] << 8 | message[4];

                    var routingPort = InputPorts.FirstOrDefault((p) => p.FeedbackMatchObject.Equals(inputNumber));

                    CurrentInputPort = routingPort;

                    break;
                case 0x05: //volume
                    _rawVolume = message.ParseVolumeResponse();
                    VolumeLevelFeedback.FireUpdate();
                    break;
                case 0x06: //mute
                    _muted = message.ParseMuteResponse();
                    MuteFeedback.FireUpdate();
                    break;
                case 0x20: // picture mode
                    _pictureMode = message.ParsePictureModeResponse();
                    PictureModeFeedback.FireUpdate();
                    break;
                default:
                    Debug.Console(0, this, "Unknown response received: {0}", ComTextHelper.GetEscapedText(message));
                    break;
            }
        }

        private void HandleAck(byte[] message)
        {

            if (!_ackStringFormats.TryGetValue(message[1], out string consoleMessageFormat))
            {
                Debug.Console(DebugLevels.DebugLevel, this, "Unknown Response: {0}", ComTextHelper.GetEscapedText(message));
                return;
            }

            Debug.LogMessage(LogEventLevel.Information, "Ack: {message}", this, string.Format(consoleMessageFormat, ComTextHelper.GetEscapedText(message)));
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

            _pollTimer.Reset(1000, pollTime);
        }
#endif
        public void MuteOn()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.MuteOn.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Reset(100, pollTime);
                return;
            }
            _muted = true;
            MuteFeedback.FireUpdate();

            CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "MUTE", 0));
            _pollTimer.Reset(100, pollTime);
        }

        public void MuteOff()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.MuteOff.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Reset(1000, pollTime);
                return;
            }
            _muted = false;
            MuteFeedback.FireUpdate();
            CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "MUTE", 1));
            _pollTimer.Reset(1000, pollTime);
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
            var scaledVolume = CrestronEnvironment.ScaleWithLimits(level, 65535, 0, maxVolumeLevel, 0);

            Debug.Console(2, this, "Input level: {0} scaled: {1}", level, scaledVolume);

            var volumeCommand = Rs232Commands.VolumeDirect;

            volumeCommand[5] = (byte)scaledVolume;

            if (_comsIsRs232)
            {
                var command = volumeCommand.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);

                _pollTimer.Reset(0, pollTime);

                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "VOLM", scaledVolume));
            _pollTimer.Reset(1000, pollTime);
        }

        private void SetVolume(int level, bool resetPoll = false)
        {
            Debug.Console(2, this, "Input level: {0}", level);

            var volumeCommand = Rs232Commands.VolumeDirect;

            volumeCommand[5] = (byte)level;

            if (_comsIsRs232)
            {
                var command = volumeCommand.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);

                if (resetPoll)
                {
                    _pollTimer.Reset(1000, pollTime);
                }

                return;
            }

            CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "VOLM", level));
            _pollTimer.Reset(1000, pollTime);
        }

        public void VolumeUp(bool pressRelease)
        {
            if (!pressRelease)
            {
                if (_volumeTimer != null)
                {
                    _volumeTimer.Stop();
                    _volumeTimer.Dispose();
                    _volumeTimer = null;
                }

                _pollTimer.Reset(1000, pollTime);
                return;
            }

            if (_comsIsRs232)
            {
                _pollTimer.Stop();

                _volumeCounter = 0;

                _volumeTimer = new CTimer(o =>
                {
                    this.LogVerbose("rawVolume: {raw:X2} maxVolume: {max:X2}", _rawVolume, maxVolumeLevel);

                    if (_rawVolume > maxVolumeLevel) return;

                    int increment = 1;

                    if (_volumeCounter > 4)
                    {
                        increment = 2;
                    }

                    if (_volumeCounter > 16)
                    {
                        increment = 4;
                    }

                    _rawVolume += increment;

                    SetVolume(_rawVolume);

                    VolumeLevelFeedback.FireUpdate();

                    _volumeCounter += 1;
                }, null, 0, 500);

                return;
            }
        }

        public void VolumeDown(bool pressRelease)
        {
            if (!pressRelease)
            {
                if (_volumeTimer != null)
                {
                    _volumeTimer.Stop();
                    _volumeTimer.Dispose();
                    _volumeTimer = null;
                }

                _pollTimer.Reset(1000, pollTime);
                return;
            }

            if (_comsIsRs232)
            {
                _pollTimer.Stop();

                _volumeCounter = 0;

                _volumeTimer = new CTimer(o =>
                {
                    this.LogVerbose("rawVolume: {raw:X2} maxVolume: {max:X2}", _rawVolume, maxVolumeLevel);

                    if (_rawVolume <= 0) return;

                    int increment = 1;

                    if (_volumeCounter > 4)
                    {
                        increment = 2;
                    }

                    if (_volumeCounter > 16)
                    {
                        increment = 4;
                    }

                    _rawVolume -= increment;

                    SetVolume(_rawVolume);

                    VolumeLevelFeedback.FireUpdate();

                    _volumeCounter += 1;

                }, null, 0, 500);

                return;
            }
        }

        #region PictureMode

        private string _pictureMode;

        public StringFeedback PictureModeFeedback { get; private set; }

        /// <summary>
        /// Select picture mode vivid
        /// </summary>
        public void PictureModeVivid()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Vivid");
                var command = Rs232Commands.PictureModeVivid.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Vivid not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode stadnard
        /// </summary>
        public void PictureModeStandard()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Standard");
                var command = Rs232Commands.PictureModeStandard.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Standard not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Cinema
        /// </summary>
        public void PictureModeCinema()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Cinema");
                var command = Rs232Commands.PictureModeCinema.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Cinema not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Cinema 2
        /// </summary>
        public void PictureModeCinema2()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Cinema 2");
                var command = Rs232Commands.PictureModeCinema2.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Cinema 2 not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Custom
        /// </summary>
        public void PictureModeCustom()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Custom");
                var command = Rs232Commands.PictureModeCustom.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Custom not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Sports
        /// </summary>
        public void PictureModeSports()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Sports");
                var command = Rs232Commands.PictureModeSports.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Sports not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Game
        /// </summary>
        public void PictureModeGame()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Game");
                var command = Rs232Commands.PictureModeGame.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Game not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Graphics
        /// </summary>
        public void PictureModeGraphics()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Graphics");
                var command = Rs232Commands.PictureModeGraphics.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Graphics not available using IP control");
            //CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }

        /// <summary>
        /// Select picture mode Toggle
        /// </summary>
        public void PictureModeToggle()
        {
            if (_comsIsRs232)
            {
                this.LogVerbose("Picture Mode Toggle");
                var command = Rs232Commands.PictureModeToggle.WithChecksum();
                _coms.SendBytes(command);
                _lastCommand = command;
                return;
            }

            this.LogVerbose("Picture Mode Toggle");
            CommandQueue.Enqueue(SimpleIpCommands.GetControlCommand(_coms, "IRCC", 110));
        }


        #endregion
    }
}
