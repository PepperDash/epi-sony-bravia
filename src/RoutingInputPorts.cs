using System;
using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace SonyBraviaEpi
{
    public static class RoutingInputPorts
    {
        public static IEnumerable<RoutingInputPort> Build(IRoutingSink sink, IBasicCommunication coms)
        {
            var hdmi1 = Commands.GetHdmi1(coms);
            var hdmi2 = Commands.GetHdmi2(coms);
            var hdmi3 = Commands.GetHdmi3(coms);
            var hdmi4 = Commands.GetHdmi4(coms);
            var hdmi5 = Commands.GetHdmi5(coms);
            var video1 = Commands.GetVideo1(coms);
            var video2 = Commands.GetVideo2(coms);
            var video3 = Commands.GetVideo3(coms);
            var component1 = Commands.GetComponent1(coms);
            var component2 = Commands.GetComponent2(coms);
            var component3 = Commands.GetComponent3(coms);
            var pc = Commands.GetPc(coms);

            var queue = SonyBraviaDevice.CommandQueue;

            return new List<RoutingInputPort>
            {
                new RoutingInputPort(
                    "hdmi1", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(() => queue.Enqueue(hdmi1)), sink) { Port = 1 },
                new RoutingInputPort(
                    "hdmi2", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(() => queue.Enqueue(hdmi2)), sink) { Port = 2 },
                new RoutingInputPort(
                    "hdmi3", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(() => queue.Enqueue(hdmi3)), sink) { Port = 3 },
                new RoutingInputPort(
                    "hdmi4", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Hdmi,
                    new Action(() => queue.Enqueue(hdmi4)), sink) { Port = 4 },
                new RoutingInputPort(
                    "hdmi5", eRoutingSignalType.AudioVideo, eRoutingPortConnectionType.Composite,
                    new Action(() => queue.Enqueue(hdmi5)), sink) { Port = 5 },
                new RoutingInputPort(
                    "pc", eRoutingSignalType.Video, eRoutingPortConnectionType.Vga,
                    new Action(() => queue.Enqueue(pc)), sink) { Port = 6 },
                new RoutingInputPort(
                    "video1", eRoutingSignalType.Video, eRoutingPortConnectionType.Composite,
                    new Action(() => queue.Enqueue(video1)), sink) { Port = 7 },
                new RoutingInputPort(
                    "video2", eRoutingSignalType.Video, eRoutingPortConnectionType.Composite,
                    new Action(() => queue.Enqueue(video2)), sink) { Port = 8 },
                new RoutingInputPort(
                    "video3", eRoutingSignalType.Video, eRoutingPortConnectionType.Component,
                    new Action(() => queue.Enqueue(video3)), sink) { Port = 9 },
                new RoutingInputPort(
                    "component1", eRoutingSignalType.Video, eRoutingPortConnectionType.Component,
                    new Action(() => queue.Enqueue(component1)), sink) { Port = 10 },
                new RoutingInputPort(
                    "component2", eRoutingSignalType.Video, eRoutingPortConnectionType.Component,
                    new Action(() => queue.Enqueue(component2)), sink) { Port = 11 },
                new RoutingInputPort(
                    "component3", eRoutingSignalType.Video, eRoutingPortConnectionType.Vga,
                    new Action(() => queue.Enqueue(component3)), sink) { Port = 12 },

            };
        }
    }
}