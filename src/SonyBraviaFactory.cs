using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;

namespace SonyBraviaEpi
{
    public class SonyBraviaFactory : EssentialsPluginDeviceFactory<SonyBraviaDevice>
    {
        public SonyBraviaFactory()
        {
            TypeNames = new List<string>() { "sonybravia" };

            MinimumEssentialsFrameworkVersion = "1.9.6";
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            Debug.Console(0, "[{0}] Building {1} plugin instance...", dc.Name, dc.Type);

            var props = dc.Properties.ToObject<SonyBraviaConfig>();
            if (props == null)
            {
                Debug.Console(0, "[{0}] Failed to build {1} plugin", dc.Name, dc.Type);
                return null;
            }

            var comms = CommFactory.CreateCommForDevice(dc);
            if (comms != null) return new SonyBraviaDevice(dc, comms);

            Debug.Console(0, "[{0}] Failed to build {1} plugin using {2}", dc.Name, dc.Type, props.Control.Method);
            return null;
        }
    }
}