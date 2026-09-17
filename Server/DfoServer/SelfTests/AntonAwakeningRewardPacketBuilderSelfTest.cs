using System;
using System.Linq;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningRewardPacketBuilderSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_REWARD_PACKET selftest ===");
            var failures = 0;

            var body = AntonAwakeningRewardPacketBuilder.Build(new[]
            {
                new AntonAwakeningRewardEntry(0x1234, 0, 0, 0x009ABCDE, 1),
            });
            var expected = new byte[]
            {
                0x01, 0x00, 0x00, 0x00,
                0x34, 0x12,
                0x00,
                0x00, 0x00, 0x00, 0x00,
                0xDE, 0xBC, 0x9A, 0x00,
                0x01, 0x00, 0x00, 0x00,
            };
            Check("single entry is the verified 19-byte body", body.SequenceEqual(expected), ref failures);

            var two = AntonAwakeningRewardPacketBuilder.Build(new[]
            {
                new AntonAwakeningRewardEntry(1, 0, 0, 10157831, 1),
                new AntonAwakeningRewardEntry(2, 0, 2, 10157833, 1),
            });
            Check("two entries use 4 + count * 15 bytes", two.Length == 34, ref failures);
            Check("two-entry count is at offset zero", BitConverter.ToUInt32(two, 0) == 2, ref failures);
            Check("second userId begins at offset 19", BitConverter.ToUInt16(two, 19) == 2, ref failures);
            Check("second flags preserve state 2", BitConverter.ToUInt32(two, 22) == 2, ref failures);
            Check("second itemId begins at offset 26", BitConverter.ToUInt32(two, 26) == 10157833, ref failures);

            Check(
                "duplicate user IDs are rejected",
                AntonAwakeningRewardPacketBuilder.Build(new[]
                {
                    new AntonAwakeningRewardEntry(1, 0, 0, 10157831, 1),
                    new AntonAwakeningRewardEntry(1, 0, 1, 10157832, 1),
                }) == null,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_REWARD_PACKET selftest passed."
                    : $"ANTON_AWAKENING_REWARD_PACKET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
