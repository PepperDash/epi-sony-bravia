using System.Collections.Generic;
using Newtonsoft.Json;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaIrConfig
    {
        [JsonProperty("warmingTimeMs")]
        public uint WarmingTimeMs { get; set; }

        [JsonProperty("coolingTimeMs")]
        public uint CoolingTimeMs { get; set; }

        [JsonProperty("friendlyNames")]
        public List<SonyBraviaIrFriendlyName> FriendlyNames { get; set; } = new List<SonyBraviaIrFriendlyName>();

        // Set true when this device is a control-only REMOTE paired with the RS232/IP sonybravia
        // device that owns the same panel: that device keeps power, inputs and feedback, and this one
        // only sends remote keys (dpad, keypad, transport, app buttons).
        //
        // Suppresses every power command this driver would send, including the implicit PowerOn
        // ExecuteSwitch fires before an input or app select. Blind IR has to assume the panel may be
        // off, which is right when IR is the display's only driver and wrong for a remote: two blind
        // power paths to one panel is how state desyncs.
        //
        // NullValueHandling.Ignore so an explicit null keeps the default rather than throwing in
        // ToObject and taking the device down with it.
        [JsonProperty("remoteOnly", NullValueHandling = NullValueHandling.Ignore)]
        public bool RemoteOnly { get; set; }

        // IR files don't all name things the same way: one maps its OK key to standard ENTER rather
        // than SELECT, another labels its HDMI inputs INPUT_7_(HDMI). Each entry maps a standard
        // command ("SELECT") or input key ("hdmi1") to the name this file uses ("ENTER",
        // "INPUT_7_(HDMI)"). Case-insensitive keys.
        [JsonProperty("irCommandMap")]
        public Dictionary<string, string> IrCommandMap { get; set; } = new Dictionary<string, string>();
    }

    public class SonyBraviaIrFriendlyName
    {
        [JsonProperty("inputKey")]
        public string InputKey { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("hideInput")]
        public bool HideInput { get; set; }
    }
}
