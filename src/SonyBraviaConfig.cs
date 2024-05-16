﻿using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using System.Collections.Generic;

namespace SonyBraviaEpi
{
    public class SonyBraviaConfig
    {
        public CommunicationMonitorConfig CommunicationMonitorProperties { get; set; }
        public ControlPropertiesConfig Control { get; set; }

        public long? WarmingTimeMs { get; set; }
        public long? CoolingTimeMs { get; set; }
        public bool ForceRs232 { get; set; }

        [JsonProperty("maxVolumeLevel")]
        public byte MaxVolumeLevel { get; set; } = 0xFF;

        [JsonProperty("activeInputs")]
        public List<SonyBraviaInputConfig> ActiveInputs { get; set; } = new List<SonyBraviaInputConfig>();
    }

    public class SonyBraviaInputConfig:IKeyName
    {
        [JsonProperty("key")]
        public string Key { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;
    }
}