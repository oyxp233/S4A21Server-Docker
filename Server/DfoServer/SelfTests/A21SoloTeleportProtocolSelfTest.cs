using DfoServer.Network.Parsers.Town;
using System;

namespace DfoServer.SelfTests
{
    public static class A21SoloTeleportProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_SOLO_TELEPORT_PROTOCOL selftest ===");
            var failures = 0;

            // A21 实抓样本：8 字节 0xFF 前缀 + town/area/x/y/direction。
            var captured = new[]
            {
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x7B, 0x06, 0x6C, 0x01, 0x05 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x2A, 0x07, 0x80, 0x01, 0x05 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x01, 0x4F, 0x02, 0xEC, 0x00, 0x05 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02, 0x02, 0x7D, 0x07, 0x4C, 0x01, 0x05 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02, 0x02, 0xA5, 0x03, 0x57, 0x01, 0x05 },
            };
            var expected = new[]
            {
                (town: 1, area: 1, x: 0x067B, y: 0x016C),
                (town: 1, area: 1, x: 0x072A, y: 0x0180),
                (town: 3, area: 1, x: 0x024F, y: 0x00EC),
                (town: 2, area: 2, x: 0x077D, y: 0x014C),
                (town: 2, area: 2, x: 0x03A5, y: 0x0157),
            };

            for (var i = 0; i < captured.Length; i++)
            {
                var index = i;
                Check(
                    $"captured 15-byte solo teleport body #{index} parses with expected fields",
                    SoloTeleportRequest.TryParse(captured[index], out var request)
                    && request.TownId == expected[index].town
                    && request.AreaId == expected[index].area
                    && request.X == expected[index].x
                    && request.Y == expected[index].y
                    && request.Direction == 0x05,
                    ref failures);
            }

            Check(
                "solo teleport rejects truncated bodies",
                !SoloTeleportRequest.TryParse(
                    new byte[14], out _)
                && !SoloTeleportRequest.TryParse(null, out _),
                ref failures);

            Console.WriteLine($"A21_SOLO_TELEPORT_PROTOCOL failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
