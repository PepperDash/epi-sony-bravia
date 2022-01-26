using System.Collections.Generic;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;

namespace SonyBraviaEpi
{
    public class SonyBraviaFactory : EssentialsDeviceFactory<SonyBraviaDevice>
    {
        public SonyBraviaFactory()
        {
            TypeNames = new List<string>() { "sonybravia" };
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            return new SonyBraviaDevice(dc);
        }
    }
}