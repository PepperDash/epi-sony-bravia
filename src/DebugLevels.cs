using System;
using System.Linq;
using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace SonyBraviaEpi
{
    /// <summary>
    /// Debug level 
    /// </summary>
    static public class DebugLevels
    {
        private const string ConsoleCommand = "setplugindebuglevel";
        private const string ConsoleHelpMessage = "set plugin debug level [deviceKey] [off | on] ([minutes])";
        private const string ConsoleHelpMessageExtended = @"SETPLUGINDEBUGLEVEL [{devicekey}] [OFF | ON] [timeOutInMinutes]
    {deviceKey} [OFF | ON] [timeOutInMinutes] - Device to set plugin debug level
    timeOutInMinutes - Set timeout for plugin debug level. Default is 15 minutes
";
        private const long DebugTimerDefaultMs = 90000; // 15-minutes (90,000-ms)

        /// <summary>
        /// Error level (0) - informational level
        /// </summary>
        public static uint ErrorLevel { get; private set; }

        /// <summary>
        /// Debug level (1) - debug level
        /// </summary>
        public static uint WarningLevel { get; private set; }

        /// <summary>
        /// Notice level (2) - verbose/silly level
        /// </summary>
        public static uint NoticeLevel { get; private set; }


        private static CTimer _debugTimer;
        private static bool _timerActive;

        private static void DefaultDebugLevels()
        {            
            CrestronConsole.ConsoleCommandResponse(@"SETPLUGINDEBUGLEVEL level defaults set");
            ErrorLevel = Convert.ToUInt16(Debug.ErrorLogLevel.Error);
            WarningLevel = Convert.ToUInt16(Debug.ErrorLogLevel.Warning);
            NoticeLevel = Convert.ToUInt16(Debug.ErrorLogLevel.Notice);
        }

        private static void SetDebugLevels(uint value)
        {
            if (value > 2)
            {
                CrestronConsole.ConsoleCommandResponse(@"SETPLUGINDEBUGLEVEL level '{0}' invalid", value);
                return;
            }

            CrestronConsole.ConsoleCommandResponse(@"SETPLUGINDEBUGLEVEL level '{0}' set", value);

            ErrorLevel = value;
            WarningLevel = value;
            NoticeLevel = value;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        static DebugLevels()
        {
            // set the default values
            DefaultDebugLevels();

            CrestronConsole.AddNewConsoleCommand(
                ProcessConsoleCommand,
                ConsoleCommand,
                ConsoleHelpMessage,
                ConsoleAccessLevelEnum.AccessOperator);
        }

        /// <summary>
        /// Sets the plugin debug level        
        /// </summary>
        /// <example>
        /// SETPLUGINDEBUGLEVEL [{devicekey}] [OFF | ON] [timeOutInMinutes]
        /// </example>
        /// <param name="command">command parameters in string format, not including the command</param>
        public static void ProcessConsoleCommand(string command)
        {
            //Debug.Console(ErrorLevel, "ProcessConsoleCommand command: '{0}'", command);

            var data = command.Split(' ');
            // used for development
            //Debug.Console(ErrorLevel, "data.Count: {0} | data.Length: {1}", data.Count(), data.Length);
            //var i = 0;
            //foreach (var item in data)
            //{
            //    Debug.Console(ErrorLevel, "data[{1}]: '{0}'", item, i);
            //    i++;
            //}

            if (data == null || data.Length == 0 || string.IsNullOrEmpty(data[0]) || data[0].Contains("?"))
            {
                CrestronConsole.ConsoleCommandResponse(ConsoleHelpMessageExtended);
                return;
            };

            var key = string.IsNullOrEmpty(data[0]) ? string.Empty : data[0];
            var param = string.IsNullOrEmpty(data[1]) ? string.Empty : data[1];            
            var timerLen = (long) ((data.Length < 3 || data[2] == null) ? DebugTimerDefaultMs : TimeSpan.FromMinutes(Convert.ToUInt16(data[2])).TotalMilliseconds);

            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(param))
                return;

            var device = DeviceManager.GetDeviceForKey(key);
            if (device == null)
            {
                CrestronConsole.ConsoleCommandResponse("SETPLUGINDEBUGLEVEL unable to get device with key: '{0}'", key);
                return;
            }

            switch (param)
            {                
                case "off":
                {
                    if (!_timerActive) break;

                    _debugTimer.Stop();
                    if (!_debugTimer.Disposed)
                        _debugTimer.Dispose();

                    _timerActive = false;

                    DefaultDebugLevels();

                    break;
                }
                case "on":
                {
                    if (_debugTimer == null)
                        _debugTimer = new CTimer(t => DefaultDebugLevels(), timerLen);
                    else
                        _debugTimer.Reset();

                    _timerActive = true;

                    SetDebugLevels(0);

                    break;
                }
                default:
                {
                    CrestronConsole.ConsoleCommandResponse("SETPLUGINDEBUGLEVEL invalid parameter: '{0}'", param);
                    break;
                }
            }
        }
    }
}