using Newtonsoft.Json;

namespace PepperDash.Essentials.Plugins.SonyBravia.Mock
{
    /// <summary>
    /// Starting state and behavior of the mock panel, from the optional "mock" block in properties
    /// </summary>
    public class MockSonyBraviaOptions
    {
        /// <summary>
        /// Creates the defaults: panel off, HDMI 1, volume 20, 50ms replies
        /// </summary>
        public MockSonyBraviaOptions()
        {
            Input = "hdmi1";
            Volume = 20;
            MaxVolume = 0xFF;
            ResponseDelayMs = 50;
        }

        /// <summary>
        /// Delay before the panel answers each command, in ms. Keep it under 100ms: the device
        /// matches each reply to the last command it sent, and some commands re-poll after 100ms.
        /// </summary>
        [JsonProperty("responseDelayMs")]
        public int ResponseDelayMs { get; set; }

        /// <summary>
        /// Whether the panel starts powered on
        /// </summary>
        [JsonProperty("powerOn")]
        public bool PowerOn { get; set; }

        /// <summary>
        /// Starting input: hdmi1-hdmi5, video1-video3, component1-component3 or vga1
        /// </summary>
        [JsonProperty("input")]
        public string Input { get; set; }

        /// <summary>
        /// Starting raw volume, 0 to maxVolume
        /// </summary>
        [JsonProperty("volume")]
        public int Volume { get; set; }

        /// <summary>
        /// Highest raw volume the panel accepts; higher levels answer "over maximum"
        /// </summary>
        [JsonProperty("maxVolume")]
        public int MaxVolume { get; set; }

        /// <summary>
        /// Whether the panel starts muted
        /// </summary>
        [JsonProperty("muted")]
        public bool Muted { get; set; }
    }
}
