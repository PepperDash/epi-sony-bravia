using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // irCommandMap with each key resolved to its standard command, so "hdmi1" and "HDMI_1" are
        // the same entry and one lookup in SendIrCommand covers both.
        private readonly Dictionary<string, string> _commandMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The reverse of irCommandMap: a file label ("PRIME_VIDEO", "CUSTOM_ON") back to the standard
        // command it stands for, so a caller passing the label directly is classified the same as one
        // passing the standard name - for remoteOnly power suppression and for the warm-up replay.
        private readonly Dictionary<string, string> _labelToStandard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private CTimer _warmupTimer;
        private CTimer _cooldownTimer;

        // The selection to re-send when warm-up ends. One slot, replaced on each switch, so only the
        // latest selection replays.
        private readonly object _switchLock = new object();
        private string _pendingSwitch;
        private int _warmupGeneration;
        private int _cooldownGeneration;

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

            BuildCommandMap();

            SetupInputs();

            DeviceManager.AddDevice(_irPort);

            // The port loads its .ir file during its own activation, which runs before this
            // device's post-activation because the port is added to DeviceManager first.
            AddPostActivationAction(CheckIrFileCommands);
        }

        private void BuildCommandMap()
        {
            foreach (var entry in _config.IrCommandMap ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    this.LogWarning("irCommandMap entry '{key}' -> '{value}' is empty; skipping", entry.Key, entry.Value);
                    continue;
                }

                var standard = SonyBraviaIrCommands.Resolve(entry.Key.Trim());
                var target = entry.Value.Trim();
                _commandMap[standard] = target;

                // A target that is itself a standard command keeps its own meaning (see Classify), and
                // where several keys share a target (PLAY and PAUSE -> PLAY_PAUSE) the first one wins.
                if (!_labelToStandard.ContainsKey(target))
                    _labelToStandard[target] = standard;
            }
        }

        /// <summary>
        /// What a command is, for power and replay decisions. Identity is the standard command it
        /// stands for: an input key resolves to its command, and a file label from irCommandMap maps
        /// back to its key - but never in a way that hides a real standard command. Final is the name
        /// actually sent. Both are checked, so neither a label nor a map entry can change how a
        /// command is treated.
        /// </summary>
        private (string Identity, string Final) Classify(string cmd)
        {
            var standard = SonyBraviaIrCommands.Resolve(cmd);
            var identity = !SonyBraviaIrCommands.IsKnownCommand(standard) && _labelToStandard.TryGetValue(standard, out var mappedFrom)
                ? mappedFrom
                : standard;

            return (identity, MapCommand(identity));
        }

        private static bool IsCycle((string Identity, string Final) c)
            => SonyBraviaIrCommands.IsInputCycle(c.Identity) || SonyBraviaIrCommands.IsInputCycle(c.Final);

        /// <summary>
        /// Logs, once and at Warning, every command this driver sends that the IR file has no
        /// standard command for. The IR port only reports a missing command per press, at Verbose,
        /// so without this a mismatched file just looks like dead buttons.
        /// </summary>
        private void CheckIrFileCommands()
        {
            if (!_irPort.DriverIsLoaded)
            {
                this.LogWarning("IR file not loaded; can't check which remote commands it supports");
                return;
            }

            var available = new HashSet<string>(_irPort.IrFileCommands ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var used = _config.RemoteOnly
                ? SonyBraviaIrCommands.RemoteCommands
                : SonyBraviaIrCommands.RemoteCommands.Concat(SonyBraviaIrCommands.PowerCommands);

            // Mapped commands are skipped: a map target can be a button label rather than a
            // standard command, and labels aren't in this list.
            var missing = used.Where(c => !_commandMap.ContainsKey(c) && !available.Contains(c)).ToList();

            var file = Path.GetFileName(_irPort.DriverFilepath);
            if (missing.Count == 0)
            {
                this.LogInformation("IR file '{file}' has every command this driver sends", file);
                return;
            }

            this.LogWarning("IR file '{file}' has no standard command for: {commands}. Those buttons won't work unless irCommandMap maps them to names the file uses",
                file, string.Join(", ", missing));
        }

        public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            LinkDisplayToApi(this, trilist, joinStart, joinMapKey, bridge);
        }

        /// <summary>
        /// Sends one command by name: an input key, a standard command or a name from irCommandMap.
        /// Power commands are routed to PowerOn/PowerOff/PowerToggle rather than sent directly, so a
        /// raw power command from devjson or a bridge gets the same remoteOnly suppression and
        /// warm-up/replay handling as the named methods.
        /// </summary>
        public void SendIrCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd))
            {
                this.LogError("SendIrCommand: command is null or empty");
                return;
            }

            if (DispatchIfPower(cmd)) return;

            // A direct input or app selection made during warm-up (devjson, a bridge) must become the
            // selection the warm-up replays, or the replay would undo it with the older one. Input-cycle
            // is relative, so replaying it would step again - it cancels the replay instead. Navigation
            // and volume never touch the replay. Outside warm-up there is nothing to replay.
            var c = Classify(cmd);
            var isCycle = IsCycle(c);
            if (isCycle || SonyBraviaIrCommands.IsInputSelection(c.Identity))
            {
                lock (_switchLock)
                {
                    if (IsWarmingUp)
                        _pendingSwitch = isCycle ? null : cmd;

                    Transmit(cmd);
                }
                return;
            }

            Transmit(cmd);
        }

        /// <summary>
        /// Routes a power command to the matching named method and returns true; returns false for
        /// anything else. Judged on both the identity and the name sent (see Classify), so neither a
        /// map entry nor a file label passed directly ("CUSTOM_ON") can slip past remoteOnly.
        /// </summary>
        private bool DispatchIfPower(string cmd)
        {
            var c = Classify(cmd);

            var power = SonyBraviaIrCommands.IsPowerCommand(c.Identity) ? c.Identity
                : SonyBraviaIrCommands.IsPowerCommand(c.Final) ? c.Final
                : null;

            if (power == null) return false;

            if (string.Equals(power, SonyBraviaIrCommands.PowerOn, StringComparison.OrdinalIgnoreCase))
                PowerOn();
            else if (string.Equals(power, SonyBraviaIrCommands.PowerOff, StringComparison.OrdinalIgnoreCase))
                PowerOff();
            else
                PowerToggle();

            return true;
        }

        private string MapCommand(string standard)
        {
            return _commandMap.TryGetValue(standard, out var mapped) ? mapped : standard;
        }

        /// <summary>
        /// Low-level send: resolves and maps the name, then pulses it on the IR port. No power
        /// handling - callers are the power methods themselves and SendIrCommand after dispatch.
        /// </summary>
        private void Transmit(string cmd)
        {
            var resolved = Classify(cmd).Final;
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

        // Power, warm-up and the post-warm-up replay all run under _switchLock, each as one unit, so
        // the commands go out in the order the operations happened and the warming/cooling state
        // always matches the last command sent. Each warm-up carries a generation; cancelling or
        // restarting it bumps the generation, so a timer callback that was already waiting for the
        // lock (stopping a CTimer doesn't cancel a callback in flight) sees it is stale and does nothing.

        public override void PowerOn()
        {
            if (SuppressPower(nameof(PowerOn))) return;

            lock (_switchLock)
                StartPowerOn();
        }

        // Caller holds _switchLock.
        private void StartPowerOn()
        {
            Transmit(SonyBraviaIrCommands.PowerOn);

            _cooldownGeneration++;
            _cooldownTimer?.Stop();
            IsCoolingDown = false;
            IsWarmingUp = true;

            var generation = ++_warmupGeneration;
            _warmupTimer?.Stop();
            _warmupTimer = new CTimer(o => OnWarmupComplete(generation), WarmupTime);
        }

        /// <summary>
        /// Ends warm-up and replays the latest selection made during it, once - unless a later power
        /// command superseded this warm-up, in which case it leaves everything to that command.
        /// </summary>
        private void OnWarmupComplete(int generation)
        {
            lock (_switchLock)
            {
                if (generation != _warmupGeneration) return;

                var pending = _pendingSwitch;
                _pendingSwitch = null;

                if (pending != null)
                    Transmit(pending);

                IsWarmingUp = false;
            }
        }

        // Caller holds _switchLock.
        private void CancelWarmup()
        {
            _pendingSwitch = null;
            _warmupGeneration++;
            _warmupTimer?.Stop();
            IsWarmingUp = false;
        }

        public override void PowerOff()
        {
            if (SuppressPower(nameof(PowerOff))) return;

            lock (_switchLock)
            {
                CancelWarmup();
                Transmit(SonyBraviaIrCommands.PowerOff);

                IsCoolingDown = true;
                var generation = ++_cooldownGeneration;
                _cooldownTimer?.Stop();
                _cooldownTimer = new CTimer(o => OnCooldownComplete(generation), CooldownTime);
            }
        }

        // Same generation guard as warm-up: a callback from an earlier cool-down that was already
        // running when it was stopped can't end a newer cool-down early.
        private void OnCooldownComplete(int generation)
        {
            lock (_switchLock)
            {
                if (generation != _cooldownGeneration) return;

                IsCoolingDown = false;
            }
        }

        public override void PowerToggle()
        {
            if (SuppressPower(nameof(PowerToggle))) return;

            // Blind IR can't tell which way a toggle goes, so treat every toggle as a possible
            // power-off: a replay after it could wake the display it just turned off.
            lock (_switchLock)
            {
                CancelWarmup();
                Transmit(SonyBraviaIrCommands.PowerToggle);
            }
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
            // An explicit null list, or a null entry, is skipped rather than allowed to throw here and
            // stop the device from being created.
            foreach (var item in _config.FriendlyNames ?? new List<SonyBraviaIrFriendlyName>())
            {
                if (string.IsNullOrEmpty(item?.InputKey))
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
        /// panel was off when the first command went out. A later switch replaces the pending
        /// re-send, and a power-off or power toggle cancels it.
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

            // A power selector is a power command, not an input: route it to the power methods so it
            // never enters the switch path (which would send it, power on, and queue it for replay).
            if (DispatchIfPower(cmd)) return;

            if (_config.RemoteOnly)
            {
                Transmit(cmd);
                this.LogDebug("ExecuteSwitch: remoteOnly is set, skipping the implicit PowerOn and warm-up re-send");
                return;
            }

            // One unit under the lock - send, store for replay, power on - so a power-off can't land
            // between storing the selection and powering on, cancel the selection, and then be undone
            // by the power-on that follows.
            // INPUT_CYCLE is relative: replaying it would step the input again, so it clears the pending
            // selection (neither it nor an older selection replays) instead of becoming it.
            var isCycle = IsCycle(Classify(cmd));

            lock (_switchLock)
            {
                Transmit(cmd);
                _pendingSwitch = isCycle ? null : cmd;
                StartPowerOn();
            }
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
