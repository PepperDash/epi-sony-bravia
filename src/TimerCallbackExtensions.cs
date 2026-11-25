using System;
using System.Threading;
using PepperDash.Core;
using PepperDash.Core.Logging;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    internal static class TimerCallbackExtensions
    {
        /// <summary>
        /// Wraps a System.Threading.Timer callback so an exception is logged against the device rather
        /// than ending the program, as an unhandled exception on a timer thread does on .NET 8
        /// </summary>
        public static TimerCallback GuardTimerCallback(this IKeyed device, string timerName, Action callback)
        {
            return o =>
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    device.LogError(ex, "Exception in {timerName} timer: {message}", timerName, ex.Message);
                }
            };
        }
    }
}
