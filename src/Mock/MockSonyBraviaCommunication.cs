using System;
using System.Collections.Generic;
using System.Text;
using PepperDash.Core;
using PepperDash.Essentials.Core.Queues;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    /// <summary>
    /// In-process stand-in for the display's serial port. Frames sent by the device are answered
    /// by a <see cref="MockSonyBraviaResponder"/> on a separate thread, one at a time and in order,
    /// like replies from a real port.
    /// </summary>
    /// <remarks>
    /// Deliberately not an ISocketStatus: the device treats a comm without socket status as RS232.
    /// </remarks>
    internal class MockSonyBraviaCommunication : IBasicCommunication
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private readonly MockSonyBraviaResponder responder;
        private readonly GenericQueue replyQueue;
        private readonly int responseDelayMs;
        private readonly object bufferLock = new object();
        private readonly List<byte> rxBuffer = new List<byte>();

        /// <summary>
        /// Creates a mock comm whose replies come from the given responder
        /// </summary>
        public MockSonyBraviaCommunication(string key, MockSonyBraviaResponder responder, int responseDelayMs)
        {
            Key = key;
            this.responder = responder;
            this.responseDelayMs = Math.Max(0, responseDelayMs);
            replyQueue = new GenericQueue(key + "-replies");
        }

        /// <inheritdoc />
        public string Key { get; private set; }

        /// <inheritdoc />
        public event EventHandler<GenericCommMethodReceiveBytesArgs> BytesReceived;

        /// <inheritdoc />
        public event EventHandler<GenericCommMethodReceiveTextArgs> TextReceived;

        /// <inheritdoc />
        public bool IsConnected { get; private set; }

        /// <inheritdoc />
        public void Connect()
        {
            IsConnected = true;
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            IsConnected = false;
        }

        /// <inheritdoc />
        public void SendText(string text)
        {
            if (text == null)
            {
                return;
            }

            SendBytes(Latin1.GetBytes(text));
        }

        /// <inheritdoc />
        public void SendBytes(byte[] bytes)
        {
            if (!IsConnected || bytes == null || bytes.Length == 0)
            {
                return;
            }

            lock (bufferLock)
            {
                rxBuffer.AddRange(bytes);

                int length;
                while ((length = MockSonyBraviaResponder.FrameLength(rxBuffer)) > 0 && rxBuffer.Count >= length)
                {
                    var frame = rxBuffer.GetRange(0, length).ToArray();
                    rxBuffer.RemoveRange(0, length);

                    if (length > 1)
                    {
                        replyQueue.Enqueue(new Rs232Response(frame, Reply));
                    }
                }
            }
        }

        // Runs on the reply queue's thread, one frame at a time, in send order
        private void Reply(byte[] frame)
        {
            if (responseDelayMs > 0)
            {
                System.Threading.Thread.Sleep(responseDelayMs);
            }

            var reply = responder.Respond(frame);
            if (reply == null || !IsConnected)
            {
                return;
            }

            var bytesHandler = BytesReceived;
            if (bytesHandler != null)
            {
                bytesHandler(this, new GenericCommMethodReceiveBytesArgs(reply));
            }

            var textHandler = TextReceived;
            if (textHandler != null)
            {
                textHandler(this, new GenericCommMethodReceiveTextArgs(Latin1.GetString(reply, 0, reply.Length)));
            }
        }
    }
}
