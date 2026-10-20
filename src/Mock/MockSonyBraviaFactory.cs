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

            MinimumEssentialsFrameworkVersion = "2.4.7";
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
            Debug.LogInformation("Building mock Sony BRAVIA display '{0}' (no hardware). Console: mocksony {0} status", dc.Key);

            return new SonyBraviaDevice(dc, comms);
        }
    }
}
