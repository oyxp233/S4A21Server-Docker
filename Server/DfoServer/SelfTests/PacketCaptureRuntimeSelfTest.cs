using DfoServer.Network;
using System;

namespace DfoServer.SelfTests
{
    internal static class PacketCaptureRuntimeSelfTest
    {
        public static int Run()
        {
            var failures = 0;

            GameNetworkConfig.Configure(Array.Empty<string>());
            Check(
                "packet capture is disabled by default",
                !GameNetworkConfig.PacketCaptureEnabled,
                ref failures);

            GameNetworkConfig.Configure(new[]
            {
                "--packet-capture",
                "capture-output",
            });
            Check(
                "packet capture requires an explicit opt-in",
                GameNetworkConfig.PacketCaptureEnabled
                    && GameNetworkConfig.PacketCaptureDir == "capture-output",
                ref failures);

            GameNetworkConfig.Configure(new[]
            {
                "--packet-capture",
                "capture-output",
                "--no-packet-capture",
            });
            Check(
                "explicit disable overrides packet capture",
                !GameNetworkConfig.PacketCaptureEnabled,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "PACKET_CAPTURE_RUNTIME selftest passed."
                    : $"PACKET_CAPTURE_RUNTIME selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
