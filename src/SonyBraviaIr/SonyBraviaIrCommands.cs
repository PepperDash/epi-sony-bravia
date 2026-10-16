using System;
using System.Collections.Generic;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    /// <summary>
    /// Crestron standard command names sent to the IR port. The .ir file maps each standard command
    /// to the TV's own code, so these names are not Sony-specific; what a given file actually
    /// supports is checked at runtime by the IR driver, which logs any command the file lacks.
    /// </summary>
    public static class SonyBraviaIrCommands
    {
        public const string PowerToggle = "POWER";
        public const string PowerOn = "POWER_ON";
        public const string PowerOff = "POWER_OFF";
        public const string KP1 = "1";
        public const string KP2 = "2";
        public const string KP3 = "3";
        public const string KP4 = "4";
        public const string KP5 = "5";
        public const string KP6 = "6";
        public const string KP7 = "7";
        public const string KP8 = "8";
        public const string KP9 = "9";
        public const string KP0 = "0";
        public const string VolumeUp = "VOL+";
        public const string VolumeDown = "VOL-";
        public const string MuteToggle = "MUTE";
        public const string ChannelUp = "CH+";
        public const string ChannelDown = "CH-";
        public const string Last = "LAST";
        public const string Home = "HOME";
        public const string Menu = "MENU";
        public const string DpadUp = "UP_ARROW";
        // DN_ARROW is the standard command name; DOWN_ARROW is not a standard command.
        public const string DpadDown = "DN_ARROW";
        public const string DpadLeft = "LEFT_ARROW";
        public const string DpadRight = "RIGHT_ARROW";
        public const string DpadSelect = "SELECT";
        public const string Enter = "ENTER";
        public const string Back = "BACK";
        public const string Exit = "EXIT";
        public const string Guide = "GUIDE";
        public const string Info = "INFO";
        public const string InputToggle = "INPUT_CYCLE";
        public const string InputHdmi1 = "HDMI_1";
        public const string InputHdmi2 = "HDMI_2";
        public const string InputHdmi3 = "HDMI_3";
        public const string InputHdmi4 = "HDMI_4";
        public const string Netflix = "NETFLIX";
        public const string PrimeVideo = "AMAZON_VIDEO";
        public const string FuncRed = "RED";
        public const string FuncGreen = "GREEN";
        public const string FuncYellow = "YELLOW";
        public const string FuncBlue = "BLUE";
        public const string Play = "PLAY";
        public const string Pause = "PAUSE";
        public const string Stop = "STOP";
        public const string FastForward = "FSCAN";
        public const string Rewind = "RSCAN";

        // The front end selects an input or app by its key in SonyBraviaIrController.Inputs
        // ("hdmi1", "primeVideo", ...). Those keys are not command names, so map them explicitly
        // rather than relying on a key happening to match the .ir file's button label - on the LG
        // driver "primeVideo" matched neither the label nor the standard command and every Prime
        // Video press failed until this mapping existed.
        public static readonly Dictionary<string, string> InputKeyCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "hdmi1", InputHdmi1 },
            { "hdmi2", InputHdmi2 },
            { "hdmi3", InputHdmi3 },
            { "hdmi4", InputHdmi4 },
            { "netflix", Netflix },
            { "primeVideo", PrimeVideo }
        };

        /// <summary>
        /// Every non-power command the controller sends, checked against the IR file at startup so a
        /// command the file lacks is reported once instead of failing silently on each press.
        /// </summary>
        public static readonly string[] RemoteCommands =
        {
            KP0, KP1, KP2, KP3, KP4, KP5, KP6, KP7, KP8, KP9,
            VolumeUp, VolumeDown, MuteToggle, ChannelUp, ChannelDown, Last, Home, Menu,
            DpadUp, DpadDown, DpadLeft, DpadRight, DpadSelect, Enter, Back, Exit, Guide, Info,
            InputHdmi1, InputHdmi2, InputHdmi3, InputHdmi4, Netflix, PrimeVideo,
            FuncRed, FuncGreen, FuncYellow, FuncBlue, Play, Pause, Stop, FastForward, Rewind
        };

        public static readonly string[] PowerCommands = { PowerToggle, PowerOn, PowerOff };

        /// <summary>
        /// True for the standard power commands, which a remoteOnly device must never send.
        /// </summary>
        public static bool IsPowerCommand(string command)
        {
            return string.Equals(command, PowerToggle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, PowerOn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, PowerOff, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves an input key to its standard command. Anything else passes through unchanged, so
        /// standard commands and hand-written selectors still reach the IR driver (and its error log).
        /// </summary>
        public static string Resolve(string selector)
        {
            if (string.IsNullOrEmpty(selector))
                return selector;

            return InputKeyCommands.TryGetValue(selector, out var command) ? command : selector;
        }
    }
}
