using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace SonyBraviaEpi
{
    public class SonyBraviaConfig
    {
        public CommunicationMonitorConfig CommunicationMonitorProperties { get; set; }
        public ControlPropertiesConfig Control { get; set; }

        public long? WarmingTimeMs { get; set; }
        public long? CoolingTimeMs { get; set; }
    }
}