using System;
using System.Collections.Generic;
using System.Linq;
using PepperDash.Core;

namespace SonyBraviaEpi
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

        public static bool ParsePowerResponse(this byte[] response)
        {            
            Debug.Console(DebugLevels.DebugLevel, "ParsePowerResponse response: {0}", ComTextHelper.GetEscapedText(response));

            if (response[2] == 0x00)
            {

                return response[3] == 0x01;                
            }

            if (response[2] == 0x02)
            {
                return response[3] != 0x00;                
            }

            return false;
        }

        public static string ParseInputResponse(this byte[] response)
        {
            // TODO [ ] actually add in parsing
            Debug.Console(DebugLevels.DebugLevel, "ParseInputResponse response: {0}", ComTextHelper.GetEscapedText(response));

            //add together the input type byte & the input number byte
            var inputNumber = response[3] << 8 | response[4];

            string input;

            if(_inputMap.TryGetValue(inputNumber,out input))
            {
                Debug.Console(DebugLevels.DebugLevel, "Got input {0}", input);
                return input;
            }

            return input;
        }

        public static int ParseVolumeResponse(this byte[] response)
        {
            Debug.Console(DebugLevels.DebugLevel, "ParseVolumeResponse response: {0}", ComTextHelper.GetEscapedText(response));
            //not a direct volume response
            if (response[3] != 0x01)
            {
                return 0;
            }

            return response[4];
        }

        /// <summary>
        /// True = isMuted
        /// </summary>
        /// <param name="response"></param>
        /// <returns></returns>
        public static bool ParseMuteResponse(this byte[] response)
        {
            Debug.Console(DebugLevels.DebugLevel, "ParseMuteResponse response: {0}", ComTextHelper.GetEscapedText(response));
            //not a direct mute response
            if (response[3] != 0x01) { return false; }

            return response[4] == 0x01;
        }
        public static string ParsePictureModeResponse(this byte[] response)
        {
            // TODO [ ] actually add in parsing
            Debug.Console(DebugLevels.DebugLevel, "ParsePictureModeResponse response: {0}", ComTextHelper.GetEscapedText(response));

            //add together the input type byte & the input number byte
            var pictureModeNumber = response[3] << 8 | response[4];

            string pictureMode;

            if (_pictureModeMap.TryGetValue(pictureModeNumber, out pictureMode))
            {
                Debug.Console(DebugLevels.DebugLevel, "Got picture mode {0}", pictureMode);
                return pictureMode;
            }

            return pictureMode;
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