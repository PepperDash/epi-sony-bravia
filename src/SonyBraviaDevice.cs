using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Queues;
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TwoWayDisplayBase = PepperDash.Essentials.Devices.Common.Displays.TwoWayDisplayBase;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaDevice : TwoWayDisplayBase, ICommunicationMonitor, IBridgeAdvanced,
        IOnline,
        IBasicVolumeWithFeedbackAdvanced,
        IHasPowerControlWithFeedback,
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

        private readonly System.Threading.Timer _pollTimer;
        private System.Threading.Timer _volumeTimer;
        private int _volumeCounter;
        private readonly IQueueMessage _powerOffCommand;
        private readonly IQueueMessage _powerOnCommand;

        private byte[] _incomingBuffer = { };

        private readonly GenericQueue _queueRs232;
        private readonly BlockingCollection<SimpleIpLine> _queueSimpleIp;
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
                    this.LogVerbose("received response: {response}", ComTextHelper.GetEscapedText(args.Bytes));

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
                // Bounded like the CrestronQueue it replaces: Add blocks while 50 lines are waiting
                _queueSimpleIp = new BlockingCollection<SimpleIpLine>(new ConcurrentQueue<SimpleIpLine>(), 50);
                var comsGather = new CommunicationGather(_coms, "\x0A");
                comsGather.LineReceived += (sender, args) =>
                {
                    try
                    {
                        _queueSimpleIp.Add(new SimpleIpLine(CurrentConnectionGeneration(), args.Text));
                    }
                    catch (InvalidOperationException)
                    {
                        // The program is stopping and the reader is gone: drop the line
                    }
                };

                // Answers never cross a reconnect: forget what's unanswered when the connection drops
                var socketStatus = _coms as ISocketStatus;
                if (socketStatus != null)
                {
                    socketStatus.ConnectionChange += (sender, args) => OnSimpleIpConnectionChange();
                }

                _powerOnCommand = SimpleIpCommands.GetControlCommand(_coms, "POWR", 1);
                _powerOffCommand = SimpleIpCommands.GetControlCommand(_coms, "POWR", 0);
                powerQuery = TrackSimpleIp("POWR", false, SimpleIpCommands.GetQueryCommand(_coms, "POWR"));
                inputQuery = TrackSimpleIp("INPT", false, SimpleIpCommands.GetQueryCommand(_coms, "INPT"));
                volumeQuery = TrackSimpleIp("VOLU", false, SimpleIpCommands.GetQueryCommand(_coms, "VOLU"));
                muteQuery = TrackSimpleIp("AMUT", false, SimpleIpCommands.GetQueryCommand(_coms, "AMUT"));
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
                    "vga1",_comsIsRs232 ? new SonyBraviaInput("vga1", "VGA 1", this, Rs232Commands.InputPc1.WithChecksum())
                    : new SonyBraviaInput("vga1", "VGA 1", this, empty)
                }
            };

            SetupInputs();


            // Background, so it can never hold the program open; it ends when the program stops (below)
            if (!_comsIsRs232)
            {
                new System.Threading.Thread(ProcessSimpleIpResponse)
                {
                    IsBackground = true,
                    Name = $"{Key}-simpleIpReader"
                }.Start();
            }

            _pollTimer = _comsIsRs232
                ? new System.Threading.Timer((o) => PollRs232(new List<byte[]> { Rs232Commands.PowerQuery.WithChecksum(), Rs232Commands.InputQuery.WithChecksum(), Rs232Commands.VolumeQuery.WithChecksum(), Rs232Commands.MuteQuery.WithChecksum() }), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
                : new System.Threading.Timer((o) => Poll(new List<IQueueMessage> { powerQuery, inputQuery, muteQuery, volumeQuery }), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            // Simple IP VOLU uses the TV's 0-100 volume scale; RS232 keeps its 0-255 default. An explicit
            // maxVolumeLevel applies to both, for feedback and for outgoing levels alike.
            maxVolumeLevel = props.MaxVolumeLevel ?? (_comsIsRs232 ? (byte)0xFF : (byte)100);

            MuteFeedback = new BoolFeedback(() => _muted);
            VolumeLevelFeedback = new IntFeedback(() => ScaleWithLimits(_rawVolume, maxVolumeLevel, 0, 65535, 0));

            PictureModeFeedback = new StringFeedback(() => _pictureMode);
            AvailablePictureModes = props.AvailablePictureModes;

            CrestronEnvironment.ProgramStatusEventHandler += type =>
            {
                try
                {
                    if (type != eProgramStatusEventType.Stopping)
                        return;

                    // The Simple IP reader exits once it has read what's already queued
                    _queueSimpleIp?.CompleteAdding();

                    _pollTimer.Dispose();
                }
                catch (Exception ex)
                {
                    this.LogError(ex, "Caught an exception at program stop: {message}", ex.Message);
                }
            };
        }

        protected override void Initialize()
        {
            try
            {
                _coms.Connect();
                CommunicationMonitor.Start();
                _pollTimer.Change(0, pollTime);
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Caught an exception at Initialize: {message}", ex.Message);
            }
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

                    WarmupTimer = new System.Timers.Timer(_warmingtimeMs) { AutoReset = false };
                    WarmupTimer.Elapsed += (s, e) =>
                    {
                        _powerIsOn = value;
                        IsWarming = false;
                        PowerIsOnFeedback.FireUpdate();
                    };
                    WarmupTimer.Start();
                }
                else
                {
                    IsCooling = true;

                    CooldownTimer = new System.Timers.Timer(_coolingTimeMs) { AutoReset = false };
                    CooldownTimer.Elapsed += (s, e) =>
                    {
                        _powerIsOn = value;
                        IsCooling = false;
                        PowerIsOnFeedback.FireUpdate();
                    };
                    CooldownTimer.Start();
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

        public ISelectableItems<string> Inputs { get; private set; }

        private bool _muted;

        private int _rawVolume;

        public BoolFeedback MuteFeedback { get; private set; }

        public IntFeedback VolumeLevelFeedback { get; private set; }

        public int RawVolumeLevel => _rawVolume;

        public eVolumeLevelUnits Units => eVolumeLevelUnits.Absolute;

        public int MaxVolumeLevel => maxVolumeLevel;

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
                _pollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                System.Threading.Thread.Sleep(500);

                var command = Rs232Commands.PowerOn.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Change(500, pollTime);
                return;
            }
            EnqueueSimpleIpControl("POWR", _powerOnCommand);
            _pollTimer.Change(1000, pollTime);
        }

        /// <summary>
        /// Turn device power off
        /// </summary>
        public override void PowerOff()
        {
            if (_comsIsRs232)
            {
                _pollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                System.Threading.Thread.Sleep(500);

                var command = Rs232Commands.PowerOff.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _pollTimer.Change(500, pollTime);
                return;
            }
            EnqueueSimpleIpControl("POWR", _powerOffCommand);
            _pollTimer.Change(1000, pollTime);
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
                _pollTimer.Change(1000, pollTime);
                return;
            }
            CommandQueue.Enqueue(TrackSimpleIp("POWR", false, SimpleIpCommands.GetQueryCommand(_coms, "POWR")));
            _pollTimer.Change(1000, pollTime);
        }

        /// <summary>
        /// Print a list of input routing ports
        /// </summary>
        public void ListRoutingInputPorts()
        {
            foreach (var inputPort in InputPorts)
            {
                this.LogInformation("inputPort key: {key}, connectionType: {connectionType}, feedbackMatchObject: {feedbackMatchObject}, port: {port}",
                    inputPort.Key, inputPort.ConnectionType, inputPort.FeedbackMatchObject, inputPort.Port);
            }
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
                    new Action(InputComponent1), this)
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 1));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 2));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 3));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 4));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Hdmi, 5));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 1));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 2));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Composite, 3));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 1));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 2));
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

            EnqueueSimpleIpControl("INPT", SimpleIpCommands.GetInputCommand(_coms, SimpleIpCommands.InputTypes.Component, 3));
        }

        /// <summary>
        /// Select PC input (VGA 1)
        /// </summary>
        public void InputVga1()
        {
            if (!_comsIsRs232) return;

            var command = Rs232Commands.InputPc1.WithChecksum();
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
            _pollTimer.Change(100, pollTime);
        }

        public void SendRs232Command(byte[] command)
        {
            // Debug.LogMessage(Serilog.Events.LogEventLevel.Verbose, "Sending command: {command}",this, command);

            _lastCommand = command;
            _coms.SendBytes(command);
            _pollTimer.Change(100, pollTime);
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
                    this.LogWarning("Unknown response received: {response}", ComTextHelper.GetEscapedText(message));
                    break;
            }
        }

        private void HandleAck(byte[] message)
        {

            if (!_ackStringFormats.TryGetValue(message[1], out string consoleMessageFormat))
            {
                this.LogDebug("Unknown Response: {response}", ComTextHelper.GetEscapedText(message));
                return;
            }

            Debug.LogMessage(LogEventLevel.Information, "Ack: {message}", this, string.Format(consoleMessageFormat, ComTextHelper.GetEscapedText(message)));
        }

        private void ProcessSimpleIpResponse()
        {
            while (true)
            {
                var lockTaken = false;
                try
                {
                    SimpleIpLine line;
                    if (!_queueSimpleIp.TryTake(out line, System.Threading.Timeout.Infinite))
                    {
                        // CompleteAdding was called and the queue is empty: the program is stopping
                        this.LogDebug("ProcessSimpleIpResponse: program stopping, Simple IP reader exiting");
                        return;
                    }

                    // Match and apply each line under the tracking lock (never while waiting in Dequeue),
                    // so a connection reset can't land between matching an answer and applying it
                    System.Threading.Monitor.Enter(_unansweredLock, ref lockTaken);
                    if (line.Generation != _connectionGeneration)
                    {
                        this.LogVerbose("ProcessSimpleIpResponse: dropping '{line}' from a previous connection", line.Text);
                        continue;
                    }

                    var response = line.Text;

                    this.LogVerbose("ProcessSimpleIpResponse: raw '{response}'", response);

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
                        // Skip it and keep reading; returning here would end this thread for good
                        this.LogDebug("ProcessSimpleIpResponse: unknown response '{response}'", response);
                        continue;
                    }

                    var type = matches.Groups["type"].Value;
                    var command = matches.Groups["command"].Value;
                    var parameters = matches.Groups["parameters"].Value;
                    this.LogVerbose("ProcessSimpleIpResponse: type-'{type}' | command-'{command}' | parameters-'{parameters}'",
                        type, command, parameters);

                    // The answer to one of our control messages only reports success or failure, not state
                    if (type == "A" && IsAnswerToControl(command))
                    {
                        this.LogVerbose("ProcessSimpleIpResponse: control answer for '{command}': '{parameters}'", command, parameters);
                        continue;
                    }

                    // display off input response:
                    // - '*SAINPTFFFFFFFFFFFFFFFF'
                    // - '*SAINPTNNNNNNNNNNNNNNNN'
                    if (parameters.Contains('F') || parameters.Contains('N')) continue;

                    switch (command)
                    {
                        case "POWR":
                            {
                                PowerIsOn = Convert.ToInt16(parameters) == 1;
                                this.LogVerbose("ProcessSimpleIpResponse: PowerIsOn == '{powerIsOn}'", PowerIsOn);
                                break;
                            }
                        case "VOLU":
                            {
                                // Query answer or notification: the volume as a 16-digit decimal
                                _rawVolume = int.Parse(parameters, NumberStyles.None, CultureInfo.InvariantCulture);
                                VolumeLevelFeedback.FireUpdate();
                                break;
                            }
                        case "AMUT":
                            {
                                // Query answer or notification: last digit 1 = muted, all zeros = not muted
                                _muted = parameters.EndsWith("1");
                                MuteFeedback.FireUpdate();
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

                                this.LogVerbose("ProcessSimpleIpResponse: inputType == '{inputType}' | inputNumber == '{inputNumber}'",
                                    inputType, inputNumber);

                                // Use the same input keys and port codes as the RS232 path, so the
                                // input list and routing feedback match either way
                                int inputCode;
                                switch (inputType)
                                {
                                    case SimpleIpCommands.InputTypes.Hdmi:
                                        {
                                            _currentInput = "hdmi" + inputNumber.ToString(CultureInfo.InvariantCulture);
                                            inputCode = 0x0400 | inputNumber;
                                            break;
                                        }
                                    case SimpleIpCommands.InputTypes.Component:
                                        {
                                            _currentInput = "component" + inputNumber.ToString(CultureInfo.InvariantCulture);
                                            inputCode = 0x0300 | inputNumber;
                                            break;
                                        }
                                    case SimpleIpCommands.InputTypes.Composite:
                                        {
                                            _currentInput = "video" + inputNumber.ToString(CultureInfo.InvariantCulture);
                                            inputCode = 0x0200 | inputNumber;
                                            break;
                                        }
                                    default:
                                        {
                                            // An input with no port here (TV, screen mirroring): report none rather than
                                            // keep the previous one. Set acknowledgements never reach this; see IsAnswerToControl.
                                            _currentInput = string.Empty;
                                            inputCode = 0;
                                            break;
                                        }
                                }

                                // No match (inputCode 0) clears the port, as the RS232 lookup does
                                CurrentInputPort = InputPorts.FirstOrDefault(p => p.FeedbackMatchObject.Equals(inputCode));
                                CurrentInputFeedback.FireUpdate();

                                // Deselect everything when the current input isn't in the list
                                foreach (var item in Inputs.Items)
                                {
                                    item.Value.IsSelected = item.Key.Equals(_currentInput);
                                }

                                Inputs.CurrentItem = _currentInput;

                                this.LogVerbose("ProcessSimpleIpResponse: _currentInput == '{currentInput}'", _currentInput);

                                break;
                            }
                        default:
                            {
                                this.LogDebug("ProcessSimpleIpResponse: unhandled response '{command}' == '{parameters}'",
                                    command, parameters);
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    this.LogError(ex, "ProcessSimpleIpResponse Exception: {message}", ex.Message);
                }
                finally
                {
                    if (lockTaken)
                    {
                        System.Threading.Monitor.Exit(_unansweredLock);
                    }
                }
            }
        }

        public void EnqueueCommand(IQueueMessage command)
        {
            CommandQueue.Enqueue(command);
        }

        // Simple IP answers a control (set) message with sixteen zeros, the same text a query answer uses
        // for power off, volume 0, unmuted or the TV input. The panel answers in the order messages were
        // sent, so record each tracked message as it is sent (query or control) and match every answer
        // to the oldest unanswered message for its FourCC; answers to controls are then skipped.
        // Each command is polled every ~8s, so a silent panel reaches this in about a minute
        private const int MaxUnansweredPerFourCc = 8;
        private readonly Dictionary<string, Queue<bool>> _unansweredIsControl = new Dictionary<string, Queue<bool>>();
        private readonly object _unansweredLock = new object();

        /// <summary>
        /// Wraps a Simple IP message so it is recorded and sent together, on the connection it goes out on
        /// </summary>
        private IQueueMessage TrackSimpleIp(string fourCc, bool isControl, IQueueMessage message)
        {
            return new TrackedSimpleIpMessage(() => SendTrackedSimpleIp(fourCc, isControl, message));
        }

        internal void EnqueueSimpleIpControl(string fourCc, IQueueMessage command)
        {
            // Nothing to send (an input with no Simple IP command): record nothing either
            if (command == null)
            {
                return;
            }

            CommandQueue.Enqueue(TrackSimpleIp(fourCc, true, command));
        }

        /// <summary>
        /// Checks the connection, records the message and sends it as one step, under the same lock
        /// that connection resets and answer matching use, so a reset can't fall between them
        /// </summary>
        private void SendTrackedSimpleIp(string fourCc, bool isControl, IQueueMessage message)
        {
            lock (_unansweredLock)
            {
                // Sending while disconnected goes nowhere and would never be answered: drop it
                if (!_coms.IsConnected)
                {
                    _sendConnectionCurrent = false;
                    return;
                }

                // First send on a new connection: start matching afresh, even if the disconnect
                // reset hasn't run yet
                if (!_sendConnectionCurrent)
                {
                    ClearSimpleIpTracking();
                    _sendConnectionCurrent = true;
                }

                Queue<bool> sent;
                if (!_unansweredIsControl.TryGetValue(fourCc, out sent))
                {
                    sent = new Queue<bool>();
                    _unansweredIsControl[fourCc] = sent;
                }

                // Limit outstanding sends rather than drop an entry, which would shift every later match.
                // The panel has stopped answering this command; clearing on the same connection would let
                // late replies match the wrong messages, so reconnect: no reply can follow onto a new
                // socket, and the disconnect resets the tracking.
                if (sent.Count >= MaxUnansweredPerFourCc)
                {
                    if (!_reconnectingForNoReply)
                    {
                        _reconnectingForNoReply = true;
                        this.LogWarning("{count} messages for {fourCc} are unanswered; reconnecting to resynchronize",
                            sent.Count, fourCc);

                        // Off this lock: the socket's status callback takes it during Disconnect
                        System.Threading.Tasks.Task.Run(() => ReconnectForNoReply());
                    }

                    return;
                }

                sent.Enqueue(isControl);
                message.Dispatch();
            }
        }

        // True while sends are going out on a connection the tracking already belongs to
        private bool _sendConnectionCurrent;

        // True from hitting the unanswered cap until the reconnect it triggers has been started
        private bool _reconnectingForNoReply;

        /// <summary>
        /// Drops and reopens the connection after the panel stopped answering, so tracking starts afresh
        /// </summary>
        private void ReconnectForNoReply()
        {
            try
            {
                _coms.Disconnect();

                // In case the status callback hasn't run yet, reset here too; IsConnected is false now
                OnSimpleIpConnectionChange();

                _coms.Connect();
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Reconnect after unanswered messages failed: {message}", ex.Message);
            }
            finally
            {
                lock (_unansweredLock)
                {
                    _reconnectingForNoReply = false;
                }
            }
        }

        /// <summary>
        /// Socket status changed: on a disconnect, forget everything unanswered so a reply lost with the
        /// connection can't shift later matches. A connect resets nothing here; the first send on the new
        /// connection does, so entries already recorded on it are never wiped.
        /// </summary>
        /// <remarks>
        /// GenericTcpIpClient raises the disconnect from its socket callback right after arming the
        /// reconnect timer (AutoReconnectIntervalMs, 5s by default), so this always runs well before
        /// the next connection exists.
        /// </remarks>
        private void OnSimpleIpConnectionChange()
        {
            lock (_unansweredLock)
            {
                if (_coms.IsConnected)
                {
                    return;
                }

                ClearSimpleIpTracking();
                _sendConnectionCurrent = false;
            }
        }

        /// <summary>
        /// Forgets every unanswered message, and any lines from the old connection not yet read.
        /// Call with _unansweredLock held.
        /// </summary>
        private void ClearSimpleIpTracking()
        {
            _unansweredIsControl.Clear();
            while (_queueSimpleIp.TryTake(out _))
            {
            }

            // Lines already read off the queue carry the old generation and are dropped
            System.Threading.Interlocked.Increment(ref _connectionGeneration);
        }

        /// <summary>
        /// The generation to stamp on a line as it arrives. Read without the tracking lock: a receive
        /// callback that waited on the lock could sit through a reset and stamp an old connection's
        /// line with the new generation.
        /// </summary>
        private int CurrentConnectionGeneration()
        {
            return System.Threading.Volatile.Read(ref _connectionGeneration);
        }

        private int _connectionGeneration;

        /// <summary>
        /// A received Simple IP line, stamped with the connection it arrived on
        /// </summary>
        private class SimpleIpLine
        {
            public SimpleIpLine(int generation, string text)
            {
                Generation = generation;
                Text = text;
            }

            public int Generation { get; private set; }

            public string Text { get; private set; }
        }

        /// <summary>
        /// Matches an answer to the oldest unanswered tracked message for its FourCC
        /// </summary>
        /// <returns>True if that message was a control, so the answer carries no state</returns>
        private bool IsAnswerToControl(string fourCc)
        {
            lock (_unansweredLock)
            {
                Queue<bool> sent;
                return _unansweredIsControl.TryGetValue(fourCc, out sent) && sent.Count > 0 && sent.Dequeue();
            }
        }

        private class TrackedSimpleIpMessage : IQueueMessage
        {
            private readonly Action _send;

            public TrackedSimpleIpMessage(Action send)
            {
                _send = send;
            }

            public void Dispatch()
            {
                _send();
            }
        }

        public void SetInput(string selector)
        {
            var input = Inputs.Items[selector];

            if (input != null)
            {
                input.Select();
            }

            _pollTimer.Change(1000, pollTime);
        }

        public void MuteOn()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.MuteOn.WithChecksum();
                _lastCommand = command;
                _coms.SendBytes(command);
                _muted = true;
                MuteFeedback.FireUpdate();
                _pollTimer.Change(100, pollTime);
                return;
            }
            _muted = true;
            MuteFeedback.FireUpdate();
            // AMUT: 1 = mute, 0 = unmute
            EnqueueSimpleIpControl("AMUT", SimpleIpCommands.GetControlCommand(_coms, "AMUT", 1));
            _pollTimer.Change(100, pollTime);
        }

        public void MuteOff()
        {
            if (_comsIsRs232)
            {
                var command = Rs232Commands.MuteOff.WithChecksum();
                _lastCommand = command;
                _muted = false;
                MuteFeedback.FireUpdate();
                _coms.SendBytes(command);
                _pollTimer.Change(1000, pollTime);
                return;
            }
            _muted = false;
            MuteFeedback.FireUpdate();
            EnqueueSimpleIpControl("AMUT", SimpleIpCommands.GetControlCommand(_coms, "AMUT", 0));
            _pollTimer.Change(1000, pollTime);
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

        /// <summary>
        /// Scales a value linearly from one range to another, clamping at the input limits. Matches
        /// CrestronEnvironment.ScaleWithLimits (int overload), including its truncating division.
        /// </summary>
        private static int ScaleWithLimits(int value, int inputUpper, int inputLower, int outputUpper, int outputLower)
        {
            if (value >= inputUpper) return outputUpper;
            if (value <= inputLower) return outputLower;

            return (int)(outputLower + (long)(value - inputLower) * (outputUpper - outputLower) / (inputUpper - inputLower));
        }

        public void SetVolume(ushort level)
        {
            var scaledVolume = ScaleWithLimits(level, 65535, 0, maxVolumeLevel, 0);

            this.LogVerbose("Input level: {level} scaled: {scaledVolume}", level, scaledVolume);

            if (_comsIsRs232)
            {
                var command = Rs232Commands.GetVolumeDirectBytes(scaledVolume);
                _lastCommand = command;
                _coms.SendBytes(command);

                _pollTimer.Change(0, pollTime);

                return;
            }

            EnqueueSimpleIpControl("VOLU", SimpleIpCommands.GetControlCommand(_coms, "VOLU", scaledVolume));
            _pollTimer.Change(1000, pollTime);
        }

        private void SetVolume(int level, bool resetPoll = false)
        {
            this.LogVerbose("Input level: {level}", level);

            if (_comsIsRs232)
            {
                var command = Rs232Commands.GetVolumeDirectBytes(level);
                _lastCommand = command;
                _coms.SendBytes(command);

                if (resetPoll)
                {
                    _pollTimer.Change(1000, pollTime);
                }

                return;
            }

            EnqueueSimpleIpControl("VOLU", SimpleIpCommands.GetControlCommand(_coms, "VOLU", level));
            _pollTimer.Change(1000, pollTime);
        }

        public void VolumeUp(bool pressRelease)
        {
            if (!pressRelease)
            {
                if (_volumeTimer != null)
                {
                    _volumeTimer.Dispose();
                    _volumeTimer = null;
                }

                _pollTimer.Change(1000, pollTime);
                return;
            }

            if (_comsIsRs232)
            {
                _pollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                _volumeCounter = 0;

                _volumeTimer = new System.Threading.Timer(o =>
                {
                    this.LogVerbose("rawVolume: {raw:X2} maxVolume: {max:X2}", _rawVolume, maxVolumeLevel);

                    if (_rawVolume > maxVolumeLevel)
                    {
                        _rawVolume = maxVolumeLevel;
                        VolumeLevelFeedback.FireUpdate();

                        if (_volumeTimer != null)
                        {
                            _volumeTimer.Dispose();
                            _volumeTimer = null;
                        }

                        return;
                    }
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

                    if (_rawVolume > maxVolumeLevel)
                    {
                        _rawVolume = maxVolumeLevel;
                    }

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
                    _volumeTimer.Dispose();
                    _volumeTimer = null;
                }

                _pollTimer.Change(1000, pollTime);
                return;
            }

            if (_comsIsRs232)
            {
                _pollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                _volumeCounter = 0;

                _volumeTimer = new System.Threading.Timer(o =>
                {
                    this.LogVerbose("rawVolume: {raw:X2} maxVolume: {max:X2}", _rawVolume, maxVolumeLevel);

                    if (_rawVolume <= 0)
                    {
                        _rawVolume = 0;
                        VolumeLevelFeedback.FireUpdate();

                        if (_volumeTimer != null)
                        {
                            _volumeTimer.Dispose();
                            _volumeTimer = null;
                        }

                        return;
                    }

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

                    if (_rawVolume < 0)
                    {
                        _rawVolume = 0;
                    }

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
