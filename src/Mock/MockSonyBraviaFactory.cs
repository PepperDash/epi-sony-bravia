using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    /// <summary>
    /// Builds the real <see cref="SonyBraviaDevice"/> on an in-process mock panel, so a room can be
    /// bench-tested without a display. A real config switches to the mock by changing only "type";
    /// any "control" block is ignored.
    /// </summary>
    public class MockSonyBraviaFactory : EssentialsPluginDeviceFactory<SonyBraviaDevice>
    {
        /// <summary>
        /// Registers the "mockSonyBravia" type, and "sonybraviamock" as an alias for configs written before the rename
        /// </summary>
        public MockSonyBraviaFactory()
        {
            TypeNames = new List<string> { "mockSonyBravia", "sonybraviamock" };

            // Keep equal to the major.minor.patch of the PepperDashEssentials PackageReference version in the
            // csproj, without any prerelease suffix: Essentials can't parse "-rc.N" and skips loading the plugin
            MinimumEssentialsFrameworkVersion = "3.0.0";
        }

        /// <inheritdoc />
        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            // The device reads its own config from dc.Properties, so make sure there is one
            if (dc.Properties == null)
            {
                dc.Properties = new JObject();
            }

            var mockToken = dc.Properties["mock"];
            var options = mockToken != null && mockToken.Type == JTokenType.Object
                ? mockToken.ToObject<MockSonyBraviaOptions>()
                : new MockSonyBraviaOptions();

            var responder = new MockSonyBraviaResponder(options);
            var comms = new MockSonyBraviaCommunication(dc.Key + "-mockComm", responder, options.ResponseDelayMs);

            MockSonyBraviaConsole.Register(dc.Key, responder);
            Debug.LogInformation("Building mock Sony BRAVIA display '{key}' (no hardware). Console: mocksony {key} status", dc.Key);

            return new SonyBraviaDevice(dc, comms);
        }
    }
}
