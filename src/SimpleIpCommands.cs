using PepperDash.Core;
using PepperDash.Essentials.Core.Queues;
using System;

namespace Pepperdash.Essentials.Plugins.SonyBravia
{
    public class SimpleIpCommands
    {
        public static readonly string Header = "*S";
        public static readonly string Footer = "\x0A";

        public enum MessageTypes
        {            
            Control = 0x43,   // Control "C" (67)
            Query = 0x45,     // Enquiries "E" (69)
            Answer = 0x41,    // Answers "A" (65) (with or without success)
            Notify = 0x4E     // Notify "N" (78)
        }

        public enum InputTypes
        {
            Hdmi = 1,
            Composite = 3,
            Component = 4,
            ScreenMirroring = 5
        }

        public static readonly int PowerOn = 0;
        public static readonly int PowerOff = 1;
        
        public static IQueueMessage GetControlCommand(IBasicCommunication coms, string cmd, int value)
        {
            // *SCPOWR00000000000000010A
            return new ComsMessage(coms, string.Format("{0}{1}{2}{3:D16}{4}", Header, Convert.ToChar(MessageTypes.Control), cmd, value, Footer));
        }

        public static IQueueMessage GetQueryCommand(IBasicCommunication coms, string cmd)
        {
            // *SEINPT################0A
            return new ComsMessage(coms, string.Format("{0}{1}{2}{3}{4}", Header, Convert.ToChar(MessageTypes.Query), cmd, new string('#', 16), Footer));
        }
        
        public static IQueueMessage GetInputCommand(IBasicCommunication coms, InputTypes inputType, int inputValue)
        {
            // *SCINPT{0:D8}{D:8}0A
            // *SCINPT00000001000000010A
            var input = string.Format("{0:D8}{1:D8}", (int) inputType, inputValue);
            return new ComsMessage(coms, string.Format("{0}{1}{2}{3}{4}", Header, Convert.ToChar(MessageTypes.Control), "INPT", input, Footer));
        }        
    }
}