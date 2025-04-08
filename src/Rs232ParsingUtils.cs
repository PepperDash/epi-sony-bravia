using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public static class Rs232ParsingUtils
    {
        private static Dictionary<int, string> _inputMap = new Dictionary<int, string>
        {
            {0x0201,"video1" },
            {0x0202,"video2" },
            {0x0203,"video3" },
            {0x0301, "component1" },
            {0x0302,"component2" },
            {0x0303,"component3" },
            {0x0401,"hdmi1" },
            {0x0402,"hdmi2" },
            {0x0403,"hdmi3" },
            {0x0404,"hdmi4" },
            {0x0405,"hdmi5" },
            {0x0501,"vga1" }            
        };

        private static Dictionary<int, string> _pictureModeMap = new Dictionary<int, string>
        {
            {0x0100,"vivid" },
            {0x0101,"standard" },
            {0x0102,"cinema" },
            {0x0103, "custom" },
            {0x0104,"cinema2" },
            {0x0105,"sports" },
            {0x0106,"game" },
            {0x0107,"graphics" }

        };
        private const byte Header = 0x70;

        public static byte[] WithChecksum(this byte[] command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }

            var checksum = 0;
            var total = command.Length;

            if (total == 0)
            {
                throw new ArgumentOutOfRangeException("command", "command array is empty");
            }

            for (var i = 0; i < total; i++)
            {
                checksum += command[i];
            }

            var response = new byte[total + 1];

            command.CopyTo(response, 0);
            response[total] = (byte)(checksum & 0xFF);

            return response;
        }

        public static bool ParsePowerResponse(this byte[] response)
        {
            Debug.LogMessage(LogEventLevel.Debug, "ParsePowerResponse response: {Response}", null, ComTextHelper.GetEscapedText(response));

            if (response != null && response.Length >= 5)
            {
                var power = response[4];
                return power == 0x02;
            }
            return false;
        }

        public static string ParseInputResponse(this byte[] response)
        {
            Debug.LogMessage(LogEventLevel.Debug, "ParseInputResponse response: {Response}", null, ComTextHelper.GetEscapedText(response));

            if (response != null && response.Length >= 8)
            {
                var inputType = response[4];
                var inputNumber = response[5];

                var input = string.Format("{0}{1}", inputType, inputNumber);
                Debug.LogMessage(LogEventLevel.Debug, "Got input {Input}", null, input);

                return input;
            }
            return string.Empty;
        }

        public static int ParseVolumeResponse(this byte[] response)
        {
            Debug.LogMessage(LogEventLevel.Debug, "ParseVolumeResponse response: {Response}", null, ComTextHelper.GetEscapedText(response));

            if (response != null && response.Length >= 5)
            {
                var volume = response[4];
                return volume;
            }
            return 0;
        }

        public static bool ParseMuteResponse(this byte[] response)
        {
            Debug.LogMessage(LogEventLevel.Debug, "ParseMuteResponse response: {Response}", null, ComTextHelper.GetEscapedText(response));

            if (response != null && response.Length >= 5)
            {
                var mute = response[4];
                return mute == 0x01;
            }
            return false;
        }

        public static string ParsePictureModeResponse(this byte[] response)
        {
            Debug.LogMessage(LogEventLevel.Debug, "ParsePictureModeResponse response: {Response}", null, ComTextHelper.GetEscapedText(response));

            if (response != null && response.Length >= 5)
            {
                var pictureMode = response[4];
                Debug.LogMessage(LogEventLevel.Debug, "Got picture mode {PictureMode}", null, pictureMode);

                return pictureMode.ToString();
            }
            return string.Empty;
        }

        public static bool IsComplete(this byte[] message)
        {
            var returnDataSize = message[2];
            var totalDataSize = returnDataSize + 3;
            return message.Length == totalDataSize;
        }

        public static bool ContainsHeader(this byte[] bytes)
        {
            return bytes.Any(IsHeader());
        }

        public static int NumberOfHeaders(this byte[] bytes)
        {
            return bytes.Count(IsHeader());
        }

        public static int FirstHeaderIndex(this byte[] bytes)
        {
            return bytes.ToList().IndexOf(Header);
        }

        private static byte[] GetFirstMessageWithMultipleHeaders(this byte[] bytes)
        {
            // any less than 3-bytes, we don't have a complete message
            if (bytes.Length < 3) return bytes;

            var secondHeaderIndex = bytes.ToList().FindIndex(1, IsHeader().ToPredicate());            

            // ex. 0x70,0x00,0x70 (valid ACK response) - skip to byte[3]
            if ((bytes[0] + bytes[1] == bytes[2]) && (bytes[2] == Header)) secondHeaderIndex++;

            if (secondHeaderIndex <= 0) secondHeaderIndex = bytes.Length;

            return bytes.Take(secondHeaderIndex).ToArray();
        }

        public static byte[] GetFirstMessage(this byte[] bytes)
        {
            return (bytes.NumberOfHeaders() <= 1) ? bytes : bytes.GetFirstMessageWithMultipleHeaders();
        }

        public static byte[] CleanToFirstHeader(this byte[] bytes)
        {
            var firstHeaderIndex = bytes.FirstHeaderIndex();
            return bytes.Skip(firstHeaderIndex).ToArray();
        }

        public static byte[] CleanOutFirstMessage(this byte[] bytes)
        {
            // any less than 3-bytes, we don't have a complete message
            if (bytes.Length < 3) return bytes;

            var secondHeaderIndex = bytes.ToList().FindIndex(1, IsHeader().ToPredicate());
            
            // ex. 0x70,0x00,0x70 (valid ACK response) - skip to byte[3]
            if ((bytes[0] + bytes[1] == bytes[2]) && (bytes[2] == Header)) secondHeaderIndex++;

            if (secondHeaderIndex <= 0) secondHeaderIndex = bytes.Length;

            return bytes.Skip(secondHeaderIndex).ToArray();
        }

        public static string ToReadableString(this byte[] bytes)
        {
            return BitConverter.ToString(bytes);
        }

        private static Func<byte, bool> IsHeader()
        {
            const byte header = Header;
            return t => t == header;
        }

        private static Predicate<T> ToPredicate<T>(this Func<T, bool> func)
        {
            return new Predicate<T>(func);
        }
    }
}