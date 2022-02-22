using System;
using System.Linq;
using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace SonyBraviaEpi
{
    /// <summary>
    /// Has debug levels interface
    /// </summary>
    public interface IHasDebugLevels
    {
        // LogLevel enum
        // - https://docs.microsoft.com/en-us/javascript/api/@aspnet/signalr/loglevel?view=signalr-js-latest

        /// <summary>
        /// Trace level (0)
        /// </summary>
        /// <remarks>
        /// Log level for very low severity diagnostic messages.
        /// </remarks>
        uint TraceLevel { get; set; }

        /// <summary>
        /// Debug level (1)
        /// </summary>
        /// <remarks>
        /// Log level for low severity diagnostic messages.
        /// </remarks>
        uint DebugLevel { get; set; }

        /// <summary>
        /// Error Level (2)
        /// </summary>
        /// <remarks>
        /// Log level for diagnostic messages that indicate a failure in the current operation.
        /// </remarks>
        uint ErrorLevel { get; set; }

        /// <summary>
        /// Sets the device debug levels to the value 
        /// </summary>
        /// <param name="value">uint value</param>
        void SetDebugLevels(uint value);

        /// <summary>
        /// Resets the device debug levels to the standard values
        /// </summary>
        void ResetDebugLevels();

        
    }

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
        /// Trace level (0)
        /// </summary>
        /// <remarks>
        /// Log level for very low severity diagnostic messages.
        /// </remarks>
        public static uint TraceLevel { get; private set; }

        /// <summary>
        /// Debug level (1)
        /// </summary>
        /// <remarks>
        /// Log level for low severity diagnostic messages.
        /// </remarks>
        public static uint DebugLevel { get; private set; }

        /// <summary>
        /// Error Level (2)
        /// </summary>
        /// <remarks>
        /// Log level for diagnostic messages that indicate a failure in the current operation.
        /// </remarks>
        public static uint ErrorLevel { get; private set; }


        private static CTimer _debugTimer;
        private static bool _timerActive;

        private static void ResetDebugLevels()
        {            
            CrestronConsole.ConsoleCommandResponse(@"SETPLUGINDEBUGLEVEL level defaults set");
            TraceLevel = Convert.ToUInt16(Debug.ErrorLogLevel.Error);
            DebugLevel = Convert.ToUInt16(Debug.ErrorLogLevel.Warning);
            ErrorLevel = Convert.ToUInt16(Debug.ErrorLogLevel.Notice);
        }

        private static void SetDebugLevels(uint value)
        {
            if (value > 2)
            {
                CrestronConsole.ConsoleCommandResponse(@"SETPLUGINDEBUGLEVEL level '{0}' invalid", value);
                return;
            }

            CrestronConsole.ConsoleCommandResponse(@"SETPLUGINDEBUGLEVEL level '{0}' set", value);

            TraceLevel = value;
            DebugLevel = value;
            ErrorLevel = value;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        static DebugLevels()
        {
            // set the default values
            ResetDebugLevels();

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
            var data = command.Split(' ');
            
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
            if (device == null || !device.Key.Equals(key))
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

                    ResetDebugLevels();

                    break;
                }
                case "on":
                {
                    if (_debugTimer == null)
                        _debugTimer = new CTimer(t => ResetDebugLevels(), timerLen);
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