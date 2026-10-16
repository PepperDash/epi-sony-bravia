using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaIrFactory : EssentialsPluginDeviceFactory<SonyBraviaIrController>
    {
        public SonyBraviaIrFactory()
        {
            TypeNames = new List<string> { "sonyBraviaIr" };

            MinimumEssentialsFrameworkVersion = "2.4.7";
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            var irPort = IRPortHelper.GetIrOutputPortController(dc);
            if (irPort == null)
            {
                Debug.LogError("[{key}] No IR output port found; cannot create {type}. Check the control block's controlPortDevKey, controlPortNumber and irFile", dc.Key, dc.Type);
                return null;
            }

            var props = dc.Properties.ToObject<SonyBraviaIrConfig>() ?? new SonyBraviaIrConfig();

            return new SonyBraviaIrController(dc.Key, dc.Name, props, irPort);
        }
    }
}
