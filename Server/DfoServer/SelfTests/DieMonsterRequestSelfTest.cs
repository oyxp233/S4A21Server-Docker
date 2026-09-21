using DfoServer.Network.Parsers.Dungeon;
using System;

namespace DfoServer.SelfTests
{
    // 样本来源: 2026-09-20 线上 packet_log 实测捕获/普通击杀包。
    public static class DieMonsterRequestSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DIE_MONSTER_REQUEST selftest ===");
            var failures = 0;

            var capturePackets = new (string Name, byte[] Body)[]
            {
                ("C1", ParseHex(
                    "1E 25 00 00 05 00 3C 11 00 00 05 00 00 00 00 7A " +
                    "25 00 19 04 00 00 02 11 01 04 00 A6 1C 06 00 1A " +
                    "00 11 01 05 00 0A 59 1F 00 FC 03 C5 02 1D 01 00 " +
                    "01 00 00 48 46 00 44 00 00 00 47 F6 28 00 00 00 " +
                    "00 00 F2 01 16 01 EC 02 2B 01 15 00 27 00 19 00 " +
                    "00 00 48 8D EB AB")),
                ("C2", ParseHex(
                    "F3 98 00 00 04 00 17 23 00 00 0B 00 00 00 23 77 " +
                    "16 00 64 00 00 00 01 11 01 04 00 70 76 16 00 64 " +
                    "00 33 05 44 01 00 01 00 00 00 0E 00 44 00 00 00 " +
                    "00 D9 4E 1B 00 00 00 00 00 00 F2 01 16 01 28 01 " +
                    "18 01 15 00 2A 00 04 00 00 00 B5 DB 98 73")),
                ("C3", ParseHex(
                    "94 CB 00 00 05 00 97 1F 00 00 06 00 00 00 3B 0F " +
                    "17 00 46 05 00 00 01 11 01 05 00 2F 0F 17 00 46 " +
                    "05 88 02 12 01 00 01 00 00 B5 79 00 44 00 00 00 " +
                    "D9 4E 1B 00 00 00 00 00 F2 01 16 01 4A 01 14 01 " +
                    "15 00 2A 00 1A 00 00 00 C1 90 F0 08")),
            };
            foreach (var (name, body) in capturePackets)
            {
                var request = DieMonsterRequest.Parse(body);
                Check(
                    $"capture packet {name} ({body.Length}B) parses IsCapture=true",
                    request.IsCapture,
                    ref failures);
            }

            var normalKillPackets = new (string Name, byte[] Body)[]
            {
                ("N1", ParseHex(
                    "1D 25 00 00 04 00 00 00 00 00 00 00 00 00 17 28 " +
                    "08 00 08 00 00 00 01 11 01 04 00 59 27 08 00 08 " +
                    "00 5F 04 23 01 00 00 00 00 E8 05 00 44 00 00 00 " +
                    "C5 47 07 00 00 00 00 00 4A 04 09 01 4F 04 23 01 " +
                    "15 00 27 00 1E 00 00 00 79 04 F1 A7")),
                ("N2", ParseHex(
                    "F2 98 00 00 04 00 00 00 00 00 00 00 00 00 51 06 " +
                    "05 00 0D 00 00 00 01 11 01 04 00 C8 05 05 00 0D " +
                    "00 6A 03 21 01 00 00 00 00 C5 4F 00 44 00 00 00 " +
                    "84 DA 04 00 00 00 00 00 4A 04 09 01 BE 03 09 01 " +
                    "15 00 2A 00 02 00 00 00 16 71 3E F9")),
                ("N3", ParseHex(
                    "93 CB 00 00 05 00 00 05 00 00 01 00 00 00 6D 75 " +
                    "0C 00 05 00 00 00 01 11 01 05 00 66 75 0C 00 05 " +
                    "00 F7 01 1E 01 00 00 00 00 74 75 00 44 00 00 00 " +
                    "84 DA 04 00 00 00 00 00 4A 04 09 01 08 03 1E 01 " +
                    "15 00 2A 00 29 00 00 00 18 AD 38 4D")),
                ("N4", ParseHex(
                    "98 24 00 00 05 00 00 00 00 00 00 00 00 00 68 9F " +
                    "07 00 03 00 00 00 02 11 01 04 00 C9 46 02 00 02 " +
                    "00 11 01 05 00 49 58 05 00 01 00 9B 02 13 01 00 " +
                    "00 00 00 B9 1C 00 45 00 00 00 9C 53 06 00 00 00 " +
                    "00 00 D6 02 23 01 C9 02 19 01 15 00 3B 00 31 00 " +
                    "00 00 C6 73 86 57")),
            };
            foreach (var (name, body) in normalKillPackets)
            {
                var request = DieMonsterRequest.Parse(body);
                Check(
                    $"normal kill packet {name} ({body.Length}B) parses IsCapture=false",
                    !request.IsCapture,
                    ref failures);
            }

            var c1 = DieMonsterRequest.Parse(capturePackets[0].Body);
            Check(
                "capture packet C1 keeps LocalIndex/UserId parsing",
                c1.LocalIndex == 0x251E && c1.UserId == 0,
                ref failures);
            var n4 = DieMonsterRequest.Parse(normalKillPackets[3].Body);
            Check(
                "normal kill packet N4 (n=2) keeps LocalIndex/UserId parsing",
                n4.LocalIndex == 0x2498 && n4.UserId == 0,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DIE_MONSTER_REQUEST selftest passed."
                    : $"DIE_MONSTER_REQUEST selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] ParseHex(string hex)
        {
            var tokens = hex.Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[tokens.Length];
            for (var i = 0; i < tokens.Length; i++)
                bytes[i] = Convert.ToByte(tokens[i], 16);
            return bytes;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
