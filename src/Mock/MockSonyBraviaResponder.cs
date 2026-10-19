using System;
using System.Collections.Generic;
using System.Linq;

namespace PepperDash.Essentials.Plugins.SonyBravia.Mock
{
    /// <summary>
    /// How the mock panel misbehaves, for exercising the device's error paths
    /// </summary>
    internal enum MockSonyBraviaFault
    {
        /// <summary>Answer every command normally</summary>
        None,
        /// <summary>Answer every command with "command cancelled" (70 03 73)</summary>
        Error,
        /// <summary>Never answer, so the communication monitor goes offline</summary>
        Silent
    }

    /// <summary>
    /// Answers Sony BRAVIA RS232 frames the way a real panel does.
    /// Holds the panel state only and uses no Crestron types, so it can be linked into tests.
    /// </summary>
    /// <remarks>
    /// Control: 8C 00 {function} {length} {data...} {checksum} -> 70 {answer} {checksum}
    /// Query:   83 00 {function} FF FF {checksum}              -> 70 00 {length} {data...} {checksum}
    /// The checksum is the low byte of the sum of every preceding byte. Answer 00 means completed;
    /// 01 over maximum, 02 under minimum, 03 cancelled (e.g. while off), 04 parse error.
    /// </remarks>
    internal class MockSonyBraviaResponder
    {
        private const byte ControlHeader = 0x8C;
        private const byte QueryHeader = 0x83;
        private const byte AnswerHeader = 0x70;

        private const byte Completed = 0x00;
        private const byte OverMaximum = 0x01;
        private const byte Cancelled = 0x03;
        private const byte ParseError = 0x04;

        private const byte FunctionPower = 0x00;
        private const byte FunctionInput = 0x02;
        private const byte FunctionVolume = 0x05;
        private const byte FunctionMute = 0x06;
        private const byte FunctionPictureMode = 0x20;

        /// <summary>
        /// Input codes (type &lt;&lt; 8 | number) and their keys, matching the device's routing ports
        /// </summary>
        public static readonly Dictionary<string, int> InputCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "video1", 0x0201 }, { "video2", 0x0202 }, { "video3", 0x0203 },
            { "component1", 0x0301 }, { "component2", 0x0302 }, { "component3", 0x0303 },
            { "hdmi1", 0x0401 }, { "hdmi2", 0x0402 }, { "hdmi3", 0x0403 }, { "hdmi4", 0x0404 }, { "hdmi5", 0x0405 },
            { "vga1", 0x0501 }
        };

        private readonly object stateLock = new object();
        private readonly int maxVolume;
        private bool powerIsOn;
        private int input;
        private int volume;
        private bool isMuted;
        private byte pictureMode;

        /// <summary>
        /// Creates a responder with the given starting state
        /// </summary>
        public MockSonyBraviaResponder(MockSonyBraviaOptions options)
        {
            options = options ?? new MockSonyBraviaOptions();

            maxVolume = Math.Max(1, Math.Min(0xFF, options.MaxVolume));
            powerIsOn = options.PowerOn;
            input = InputCodes.ContainsKey(options.Input ?? string.Empty) ? InputCodes[options.Input] : 0x0401;
            volume = Math.Max(0, Math.Min(maxVolume, options.Volume));
            isMuted = options.Muted;
            pictureMode = 0x01; // standard
        }

        /// <summary>
        /// Current fault mode
        /// </summary>
        public MockSonyBraviaFault Fault { get; set; }

        /// <summary>
        /// Number of bytes in the frame that starts at buffer[0], or 0 if it can't be known yet
        /// </summary>
        public static int FrameLength(IList<byte> buffer)
        {
            if (buffer.Count == 0) return 0;
            if (buffer[0] == QueryHeader) return 6;
            if (buffer[0] == ControlHeader) return buffer.Count >= 4 ? 4 + buffer[3] : 0; // length counts data + checksum
            return 1; // not a header: drop the byte
        }

        /// <summary>
        /// Answers one complete frame
        /// </summary>
        /// <returns>The reply frame, or null when the panel stays silent</returns>
        public byte[] Respond(byte[] frame)
        {
            if (frame == null || frame.Length < 5 || (frame[0] != ControlHeader && frame[0] != QueryHeader))
            {
                return null;
            }

            lock (stateLock)
            {
                if (Fault == MockSonyBraviaFault.Silent)
                {
                    return null;
                }

                if (Checksum(frame, frame.Length - 1) != frame[frame.Length - 1])
                {
                    return Answer(ParseError);
                }

                var function = frame[2];
                if (!IsKnown(function))
                {
                    // Unknown functions get no reply, so gaps in the mock show up as unanswered commands
                    return null;
                }

                if (Fault == MockSonyBraviaFault.Error || (function != FunctionPower && !powerIsOn))
                {
                    return Answer(Cancelled);
                }

                return frame[0] == QueryHeader ? Query(function) : Control(function, frame);
            }
        }

        /// <summary>
        /// Changes state as if someone used the panel's own buttons or remote
        /// </summary>
        public void SetPower(bool on)
        {
            lock (stateLock) powerIsOn = on;
        }

        /// <summary>
        /// Changes the input as if someone used the panel's own buttons or remote
        /// </summary>
        /// <returns>False if the input key is not one the panel has</returns>
        public bool SetInput(string inputKey)
        {
            int code;
            if (!InputCodes.TryGetValue(inputKey ?? string.Empty, out code))
            {
                return false;
            }

            lock (stateLock) input = code;
            return true;
        }

        /// <summary>
        /// Changes the raw volume as if someone used the panel's own buttons or remote
        /// </summary>
        public void SetVolume(int level)
        {
            lock (stateLock) volume = Math.Max(0, Math.Min(maxVolume, level));
        }

        /// <summary>
        /// Changes the mute state as if someone used the panel's own buttons or remote
        /// </summary>
        public void SetMute(bool muted)
        {
            lock (stateLock) isMuted = muted;
        }

        /// <summary>
        /// One-line description of the panel state, for the console
        /// </summary>
        public string Describe()
        {
            lock (stateLock)
            {
                var inputKey = InputCodes.FirstOrDefault(i => i.Value == input).Key;
                return string.Format("power={0} input={1} volume={2}/{3} mute={4} pictureMode=0x{5:X2} fault={6}",
                    powerIsOn ? "on" : "off", inputKey, volume, maxVolume, isMuted, pictureMode, Fault);
            }
        }

        private static bool IsKnown(byte function)
        {
            return function == FunctionPower || function == FunctionInput || function == FunctionVolume ||
                   function == FunctionMute || function == FunctionPictureMode;
        }

        private byte[] Query(byte function)
        {
            switch (function)
            {
                case FunctionPower:
                    return Data((byte)(powerIsOn ? 0x01 : 0x00));
                case FunctionInput:
                    return Data((byte)(input >> 8), (byte)(input & 0xFF));
                case FunctionVolume:
                    return Data(0x01, (byte)volume);
                case FunctionMute:
                    return Data(0x01, (byte)(isMuted ? 0x01 : 0x00));
                default:
                    // Picture mode has no query on a real panel
                    return Answer(ParseError);
            }
        }

        private byte[] Control(byte function, byte[] frame)
        {
            // frame: 8C 00 {function} {length} {data...} {checksum}; length counts data + checksum
            var data = frame.Skip(4).Take(frame.Length - 5).ToArray();

            switch (function)
            {
                case FunctionPower:
                    if (data.Length != 1 || data[0] > 0x01) return Answer(ParseError);
                    powerIsOn = data[0] == 0x01;
                    return Answer(Completed);

                case FunctionInput:
                    if (data.Length != 2) return Answer(ParseError);
                    var code = data[0] << 8 | data[1];
                    if (!InputCodes.ContainsValue(code)) return Answer(ParseError);
                    input = code;
                    return Answer(Completed);

                case FunctionVolume:
                    if (data.Length != 2) return Answer(ParseError);
                    if (data[0] == 0x00)
                    {
                        // 00 = up, 01 = down
                        volume = Math.Max(0, Math.Min(maxVolume, volume + (data[1] == 0x00 ? 1 : -1)));
                        return Answer(Completed);
                    }
                    if (data[1] > maxVolume) return Answer(OverMaximum);
                    volume = data[1];
                    return Answer(Completed);

                case FunctionMute:
                    if (data.Length == 1) isMuted = !isMuted; // toggle
                    else if (data.Length == 2 && data[1] <= 0x01) isMuted = data[1] == 0x01;
                    else return Answer(ParseError);
                    return Answer(Completed);

                case FunctionPictureMode:
                    if (data.Length == 1) pictureMode = (byte)((pictureMode + 1) % 0x0A); // toggle
                    else if (data.Length == 2) pictureMode = data[1];
                    else return Answer(ParseError);
                    return Answer(Completed);

                default:
                    return null;
            }
        }

        private static byte[] Answer(byte code)
        {
            return WithChecksum(new[] { AnswerHeader, code });
        }

        private static byte[] Data(params byte[] data)
        {
            var frame = new List<byte> { AnswerHeader, Completed, (byte)(data.Length + 1) };
            frame.AddRange(data);
            return WithChecksum(frame.ToArray());
        }

        private static byte[] WithChecksum(byte[] frame)
        {
            var result = new byte[frame.Length + 1];
            frame.CopyTo(result, 0);
            result[frame.Length] = Checksum(frame, frame.Length);
            return result;
        }

        private static byte Checksum(IList<byte> bytes, int count)
        {
            var sum = 0;
            for (var i = 0; i < count; i++) sum += bytes[i];
            return (byte)(sum & 0xFF);
        }
    }
}
