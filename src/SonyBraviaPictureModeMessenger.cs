using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.AppServer.Messengers;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace SonyBraviaEpi
{
    class SonyBraviaPictureModeMessenger : MessengerBase
    {
        private readonly SonyBraviaDevice device;

        public SonyBraviaPictureModeMessenger(string key, string messagePath, SonyBraviaDevice device) : base(key, messagePath, device)
        {
            this.device = device;
        }

        protected override void RegisterActions()
        {
            AddAction("/fullStatus", (id, content) =>
            {
                this.LogVerbose("Handling full status request");
                var message = new SonyBraviaPictureModesStatus
                {
                    Mode = device.PictureModeFeedback.StringValue
                };
                PostStatusMessage(message);
            });

            // "/device/{device-key}/picturemode"
            AddAction("/pictureMode", (id, content) =>
            {
                this.LogVerbose("Handling picture mode request");
                var request = content.ToObject<SonyBraviaPictureModesRequest>();
                switch (request.Mode)
                {
                    case "standard":
                        device.PictureModeStandard();
                        break;
                    case "vivid":
                        device.PictureModeVivid();
                        break;
                    case "cinema":
                        device.PictureModeCinema();
                        break;
                    case "custom":
                        device.PictureModeCustom();
                        break;
                    default:
                        this.LogDebug("Unknown picture mode requested: {0}", request.Mode);
                        break;
                }
            });

            device.PictureModeFeedback.OutputChange += (o, a) =>
            {
                var message = new SonyBraviaPictureModesStatus
                {
                    Mode = device.PictureModeFeedback.StringValue
                };
                var jt = JToken.FromObject(message);
                PostStatusMessage(jt);
            };
        }
    }

    class SonyBraviaPictureModesStatus : DeviceStateMessageBase
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }

        [JsonProperty("availablePictureModes")]
        public List<KeyValuePair<string, string>> AvailablePictureModes { get; set; } = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Standard", "standard"),
            new KeyValuePair<string, string>("Vivid", "vivid"),
            new KeyValuePair<string, string>("Cinema", "cinema"),
            new KeyValuePair<string, string>("Custom", "custom")
        };
    }

    class SonyBraviaPictureModesRequest
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }
    }
}


