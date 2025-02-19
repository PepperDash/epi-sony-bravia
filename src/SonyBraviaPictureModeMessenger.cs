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
                var message = new SonyBraviaPictureModeStatus
                {
                    Mode = device.PictureModeFeedback.StringValue
                };
                PostStatusMessage(message);
            });

            // "/device/{device-key}/picturemode"
            AddAction("/pictureMode", (id, content) =>
            {
                this.LogVerbose("Handling picture mode request");
                var request = content.ToObject<SonyBraviaPictureModeRequest>();
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
                    case "cinema2": 
                        device.PictureModeCinema2();
                        break;
                    case "sports": 
                        device.PictureModeSports();
                        break;
                    case "game": 
                        device.PictureModeGame();
                        break;
                    case "graphics": 
                        device.PictureModeGraphics();
                        break;
                    case "custom":
                        device.PictureModeCustom();
                        break;
                    case "toggle": 
                        device.PictureModeToggle();
                        break;
                    default:
                        this.LogDebug("Unknown picture mode requested: {0}", request.Mode);
                        break;
                }
            });

            device.PictureModeFeedback.OutputChange += (o, a) =>
            {
                var message = new SonyBraviaPictureModeStatus
                {
                    Mode = device.PictureModeFeedback.StringValue
                };
                var jt = JToken.FromObject(message);
                PostStatusMessage(jt);
            };
        }
    }

    class SonyBraviaPictureModeStatus : DeviceStateMessageBase
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }

        [JsonProperty("availablePictureModes")]
        public List<IdLabel> AvailablePictureModes { get; set; } = new List<IdLabel>
        {
            new IdLabel { Id = "standard", Label = "Standard" },
            new IdLabel { Id = "vivid", Label = "Vivid" },
            new IdLabel { Id = "cinema", Label = "Cinema" },
            new IdLabel { Id = "custom", Label = "Custom" }
        };

        // public Dictionary<string, string> AvailablePictureModesDict { get; set; } = new Dictionary<string, string>
        // {
        //     { "standard", "Standard" },
        //     { "vivid", "Vivid" },
        //     { "cinema", "Cinema" },
        //     { "cinema2", "Cinema 2" },
        //     { "sports", "Sports" },
        //     { "game", "Game" },
        //     { "graphics", "Graphics" },
        //     { "custom", "Custom" }
        // };
    }

    class SonyBraviaPictureModeRequest
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }
    }

    class IdLabel
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }
    }
}


