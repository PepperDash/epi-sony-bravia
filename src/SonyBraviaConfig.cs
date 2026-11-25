using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using System.Collections.Generic;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaConfig
    {
        public CommunicationMonitorConfig CommunicationMonitorProperties { get; set; }
        public ControlPropertiesConfig Control { get; set; }

        [JsonProperty("warmingTimeMs")]
        public long? WarmingTimeMs { get; set; }

        [JsonProperty("coolingTimeMs")]
        public long? CoolingTimeMs { get; set; }
        public bool ForceRs232 { get; set; }

        /// <summary>
        /// Highest raw volume level. When omitted, RS232 uses 255 and Simple IP uses 100, the TV's own
        /// volume scale (Sony's REST getVolumeInformation reports maxVolume 100)
        /// </summary>
        [JsonProperty("maxVolumeLevel")]
        public byte? MaxVolumeLevel { get; set; }

        [JsonProperty("activeInputs")]
        public List<SonyBraviaInputConfig> ActiveInputs { get; set; } = new List<SonyBraviaInputConfig>();

        [JsonProperty("availablePictureModes")]
        public List<SonyBraviaInputConfig> AvailablePictureModes { get; set; } = new List<SonyBraviaInputConfig>();
    }

    public class SonyBraviaInputConfig:IKeyName
    {
        [JsonProperty("key")]
        public string Key { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;
    }
}