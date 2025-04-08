using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using Serilog.Events;
using System;
using System.Collections.Generic;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaFactory : EssentialsPluginDeviceFactory<SonyBraviaDevice>
    {
        public SonyBraviaFactory()
        {
            MinimumEssentialsFrameworkVersion = "1.9.7";
            TypeNames = new List<string> { "sonybravia", "sonybraviaip", "sonybraviaRS232" };
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            Debug.LogMessage(LogEventLevel.Verbose, "[{Key}] Building {Type} plugin instance...", null, dc.Key, dc.Type);

            try
            {
                var props = dc.Properties.ToObject<SonyBraviaConfig>();
                var cresnetId = props.CresnetId;
                if (!string.IsNullOrEmpty(cresnetId))
                {
                    Debug.LogMessage(LogEventLevel.Verbose, "[{Key}] Failed to build {Type} plugin", null, dc.Key, dc.Type);
                    return null;
                }

                var comms = CommFactory.CreateCommForDevice(dc);
                if (comms == null)
                {
                    Debug.LogMessage(LogEventLevel.Verbose, "[{Key}] Failed to build {Type} plugin using {Method}", null, dc.Key, dc.Type, props.Control.Method);
                    return null;
                }
                return new SonyBraviaDevice(dc, comms);
            }
            catch (Exception ex)
            {
                Debug.LogMessage(LogEventLevel.Error, "Unable to create Sony Bravia device: {0}", null, ex.Message);
                return null;
            }
        }
    }
}