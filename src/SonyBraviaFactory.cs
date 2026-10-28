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

            // Keep equal to the major.minor.patch of the PepperDashEssentials PackageReference version in the
            // csproj, without any prerelease suffix: Essentials can't parse "-rc.N" and skips loading the plugin
            MinimumEssentialsFrameworkVersion = "3.0.0";
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            Debug.LogDebug("[{key}] Building {type} plugin instance", dc.Key, dc.Type);

            var props = dc.Properties.ToObject<SonyBraviaConfig>();
            if (props == null)
            {
                Debug.LogError("[{key}] Failed to build {type} plugin: no properties", dc.Key, dc.Type);
                return null;
            }

            var comms = CommFactory.CreateCommForDevice(dc);
            if (comms != null) return new SonyBraviaDevice(dc, comms);

            Debug.LogError("[{key}] Failed to build {type} plugin using {method}", dc.Key, dc.Type, props.Control.Method);
            return null;
        }
    }
}