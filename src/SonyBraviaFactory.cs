using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using System.Collections.Generic;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaFactory : EssentialsPluginDeviceFactory<SonyBraviaDevice>
    {
        public SonyBraviaFactory()
        {
            TypeNames = new List<string> { "sonybravia", "sonybraviarest", "sonybraviasimpleip" };

            MinimumEssentialsFrameworkVersion = "1.8.5";
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            Debug.Console(DebugLevels.TraceLevel, "[{0}] Building {1} plugin instance...", dc.Key, dc.Type);

            var props = dc.Properties.ToObject<SonyBraviaConfig>();
            if (props == null)
            {
                Debug.Console(DebugLevels.TraceLevel, "[{0}] Failed to build {1} plugin", dc.Key, dc.Type);
                return null;
            }

            var comms = CommFactory.CreateCommForDevice(dc);
            if (comms != null) return new SonyBraviaDevice(dc, comms);

            Debug.Console(DebugLevels.TraceLevel, "[{0}] Failed to build {1} plugin using {2}", dc.Key, dc.Type, props.Control.Method);
            return null;
        }
    }
}