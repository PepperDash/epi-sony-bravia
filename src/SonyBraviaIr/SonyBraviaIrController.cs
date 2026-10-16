using System;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using DisplayBase = PepperDash.Essentials.Devices.Common.Displays.DisplayBase;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    /// <summary>
    /// IR control for a Sony Bravia. Usually deployed as a control-only remote (remoteOnly: true)
    /// paired through the room's displayRemotes with the sonybravia RS232/IP device, which keeps
    /// power, inputs and feedback while this device sends the remote keys RS232 has no command for.
    /// </summary>
    public class SonyBraviaIrController : DisplayBase, IBasicVolumeControls, IBridgeAdvanced, IHasInputs<string>,
        IDPad, INumericKeypad, ITransport, IChannel, IColor
    {
        private readonly SonyBraviaIrConfig _config;
        private readonly IrOutputPortController _irPort;

        private CTimer _warmupTimer;
        private CTimer _cooldownTimer;

        public ISelectableItems<string> Inputs { get; private set; }

        private bool _isWarmingUp;
        public bool IsWarmingUp
        {
            get { return _isWarmingUp; }
            private set
            {
                _isWarmingUp = value;
                IsWarmingUpFeedback.FireUpdate();
            }
        }

        private bool _isCoolingDown;
        public bool IsCoolingDown
        {
            get { return _isCoolingDown; }
            private set
            {
                _isCoolingDown = value;
                IsCoolingDownFeedback.FireUpdate();
            }
        }

        public SonyBraviaIrController(string key, string name, SonyBraviaIrConfig config, IrOutputPortController irPort)
            : base(key, name)
        {
            _config = config;
            _irPort = irPort;

            WarmupTime = _config.WarmingTimeMs > 0 ? _config.WarmingTimeMs : 8000;
            CooldownTime = _config.CoolingTimeMs > 0 ? _config.CoolingTimeMs : 8000;

            SetupInputs();

            DeviceManager.AddDevice(_irPort);
        }

        public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            LinkDisplayToApi(this, trilist, joinStart, joinMapKey, bridge);
        }

        /// <summary>
        /// Sends one IR command as a press and release. Input keys are resolved to their standard
        /// command here, so every caller - including ExecuteSwitch with a raw Inputs key - goes
        /// through the same mapping.
        /// </summary>
        public void SendIrCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd))
            {
                this.LogError("SendIrCommand: command is null or empty");
                return;
            }

            var resolved = SonyBraviaIrCommands.Resolve(cmd);
            if (!string.Equals(resolved, cmd, StringComparison.Ordinal))
                this.LogVerbose("SendIrCommand: resolved '{selector}' to '{command}'", cmd, resolved);

            this.LogDebug("SendIrCommand: '{command}'", resolved);

            _irPort.PressRelease(resolved, true);
            _irPort.PressRelease(resolved, false);
        }

        // Remote keys fire once, on release, because SendIrCommand already pulses press and release.
        // Release rather than press to match the LG IR driver, which is the bench-verified behavior.
        private void Press(bool pressRelease, string cmd)
        {
            if (pressRelease) return;
            SendIrCommand(cmd);
        }

        #region Power

        /// <summary>
        /// True when this device is a control-only remote, so the paired driver owns power.
        /// Logged because a swallowed power command otherwise looks exactly like a dead emitter.
        /// </summary>
        private bool SuppressPower(string caller)
        {
            if (!_config.RemoteOnly) return false;

            this.LogDebug("{caller} suppressed: remoteOnly is set, so the paired display driver owns power", caller);
            return true;
        }

        public override void PowerOn()
        {
            if (SuppressPower(nameof(PowerOn))) return;

            SendIrCommand(SonyBraviaIrCommands.PowerOn);

            _cooldownTimer?.Stop();
            IsCoolingDown = false;
            IsWarmingUp = true;
            _warmupTimer?.Stop();
            _warmupTimer = new CTimer(o => IsWarmingUp = false, WarmupTime);
        }

        public override void PowerOff()
        {
            if (SuppressPower(nameof(PowerOff))) return;

            SendIrCommand(SonyBraviaIrCommands.PowerOff);

            _warmupTimer?.Stop();
            IsWarmingUp = false;
            IsCoolingDown = true;
            _cooldownTimer?.Stop();
            _cooldownTimer = new CTimer(o => IsCoolingDown = false, CooldownTime);
        }

        public override void PowerToggle()
        {
            if (SuppressPower(nameof(PowerToggle))) return;

            SendIrCommand(SonyBraviaIrCommands.PowerToggle);
        }

        protected override Func<bool> IsCoolingDownFeedbackFunc => () => IsCoolingDown;

        protected override Func<bool> IsWarmingUpFeedbackFunc => () => IsWarmingUp;

        #endregion

        #region Inputs

        private void SetupInputs()
        {
            AddInputPort(RoutingPortNames.HdmiIn1, eRoutingPortConnectionType.Hdmi, SonyBraviaIrCommands.InputHdmi1);
            AddInputPort(RoutingPortNames.HdmiIn2, eRoutingPortConnectionType.Hdmi, SonyBraviaIrCommands.InputHdmi2);
            AddInputPort(RoutingPortNames.HdmiIn3, eRoutingPortConnectionType.Hdmi, SonyBraviaIrCommands.InputHdmi3);
            AddInputPort(RoutingPortNames.HdmiIn4, eRoutingPortConnectionType.Hdmi, SonyBraviaIrCommands.InputHdmi4);
            AddInputPort("netflix", eRoutingPortConnectionType.Streaming, SonyBraviaIrCommands.Netflix);
            AddInputPort("primeVideo", eRoutingPortConnectionType.Streaming, SonyBraviaIrCommands.PrimeVideo);

            // Keys match the panel app's media-app keys (netflix, primeVideo), so these entries are
            // what the user Apps tab lists when displayControlsUserMediaButtons names them.
            Inputs = new SonyBraviaIrInputs
            {
                Items = new Dictionary<string, ISelectableItem>
                {
                    { "hdmi1", new SonyBraviaIrInput("hdmi1", "HDMI 1", this) },
                    { "hdmi2", new SonyBraviaIrInput("hdmi2", "HDMI 2", this) },
                    { "hdmi3", new SonyBraviaIrInput("hdmi3", "HDMI 3", this) },
                    { "hdmi4", new SonyBraviaIrInput("hdmi4", "HDMI 4", this) },
                    { "netflix", new SonyBraviaIrInput("netflix", "Netflix", this) },
                    { "primeVideo", new SonyBraviaIrInput("primeVideo", "Prime Video", this) }
                }
            };

            ApplyFriendlyNames();
        }

        private void AddInputPort(string key, eRoutingPortConnectionType connectionType, string command)
        {
            var port = new RoutingInputPort(key, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                connectionType, command, this)
            {
                FeedbackMatchObject = command
            };
            InputPorts.Add(port);
        }

        private void ApplyFriendlyNames()
        {
            foreach (var item in _config.FriendlyNames)
            {
                if (string.IsNullOrEmpty(item.InputKey))
                {
                    this.LogWarning("friendlyNames entry has no inputKey; skipping");
                    continue;
                }

                if (!Inputs.Items.ContainsKey(item.InputKey))
                {
                    this.LogWarning("friendlyNames inputKey '{inputKey}' is not an input of this device; skipping", item.InputKey);
                    continue;
                }

                if (item.HideInput)
                    Inputs.Items.Remove(item.InputKey);
                else
                    Inputs.Items[item.InputKey] = new SonyBraviaIrInput(item.InputKey, item.Name, this);
            }
        }

        /// <summary>
        /// Sends the input or app command. Unless this device is a remote, it also powers the panel
        /// on and re-sends the command once warm-up ends, because blind IR can't know whether the
        /// panel was off when the first command went out.
        /// </summary>
        public override void ExecuteSwitch(object selector)
        {
            var cmd = selector is RoutingInputPort port
                ? port.FeedbackMatchObject as string
                : selector?.ToString();

            if (string.IsNullOrEmpty(cmd))
            {
                this.LogError("ExecuteSwitch: no command for selector '{selector}'", selector);
                return;
            }

            SendIrCommand(cmd);

            if (_config.RemoteOnly)
            {
                this.LogDebug("ExecuteSwitch: remoteOnly is set, skipping the implicit PowerOn and warm-up re-send");
                return;
            }

            EventHandler<FeedbackEventArgs> handler = null;
            handler = (o, a) =>
            {
                if (IsWarmingUp) return;

                IsWarmingUpFeedback.OutputChange -= handler;
                SendIrCommand(cmd);
            };
            IsWarmingUpFeedback.OutputChange += handler;
            PowerOn();
        }

        #endregion

        #region IBasicVolumeControls

        public void VolumeUp(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.VolumeUp);

        public void VolumeDown(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.VolumeDown);

        public void MuteToggle() => SendIrCommand(SonyBraviaIrCommands.MuteToggle);

        #endregion

        #region IDPad

        public void Up(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.DpadUp);

        public void Down(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.DpadDown);

        public void Left(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.DpadLeft);

        public void Right(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.DpadRight);

        public void Select(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.DpadSelect);

        public void Menu(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Menu);

        /// <summary>Satisfies both IDPad.Exit and IChannel.Exit.</summary>
        public void Exit(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Exit);

        #endregion

        #region INumericKeypad

        public void Digit0(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP0);
        public void Digit1(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP1);
        public void Digit2(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP2);
        public void Digit3(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP3);
        public void Digit4(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP4);
        public void Digit5(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP5);
        public void Digit6(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP6);
        public void Digit7(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP7);
        public void Digit8(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP8);
        public void Digit9(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.KP9);

        // A TV remote has no keypad accessory buttons (set-top-box Dash/Enter); the front end hides
        // them when these report false.
        public bool HasKeypadAccessoryButton1 => false;
        public string KeypadAccessoryButton1Label => string.Empty;
        public void KeypadAccessoryButton1(bool pressRelease) { }

        public bool HasKeypadAccessoryButton2 => false;
        public string KeypadAccessoryButton2Label => string.Empty;
        public void KeypadAccessoryButton2(bool pressRelease) { }

        #endregion

        #region ITransport

        public void Play(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Play);
        public void Pause(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Pause);
        public void Stop(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Stop);
        public void Rewind(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Rewind);
        public void FFwd(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.FastForward);

        // No TV-remote equivalent; no-ops, matching the LG and AppleTV drivers.
        public void ChapMinus(bool pressRelease) { }
        public void ChapPlus(bool pressRelease) { }
        public void Record(bool pressRelease) { }

        #endregion

        #region IChannel

        public void ChannelUp(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.ChannelUp);
        public void ChannelDown(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.ChannelDown);
        public void LastChannel(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Last);
        public void Guide(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Guide);
        public void Info(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Info);

        #endregion

        #region IColor

        public void Red(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.FuncRed);
        public void Green(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.FuncGreen);
        public void Yellow(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.FuncYellow);
        public void Blue(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.FuncBlue);

        #endregion

        #region Remote keys with no core interface yet

        // IDPad only covers Menu/Exit, so Home, Back and Enter are plain methods, callable over
        // devjson. Same as the LG IR driver; the panel needs a core interface to show them.
        public void Home(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Home);
        public void Back(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Back);
        public void Enter(bool pressRelease) => Press(pressRelease, SonyBraviaIrCommands.Enter);

        #endregion
    }
}
