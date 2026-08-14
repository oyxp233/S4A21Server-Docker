using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class ClearQuestListPacketSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CLEAR_QUEST_LIST_PACKET selftest ===");

            var snapshot = new SelectCharacterDataSnapshot();
            snapshot.InitializationSnapshot.CharacInvisibleFalgsPayloadLen = 21000;
            snapshot.InitializationSnapshot.CharacInvisibleFalgs.Add(
                new CharacInvisibleFalgEntrySnapshot { SlotIndex = 13502, FlagValue = 1 });
            snapshot.InitializationSnapshot.CharacInvisibleFalgs.Add(
                new CharacInvisibleFalgEntrySnapshot { SlotIndex = 29999, FlagValue = 7 });
            snapshot.InitializationSnapshot.CharacInvisibleFalgs.Add(
                new CharacInvisibleFalgEntrySnapshot { SlotIndex = 30000, FlagValue = 9 });

            var builder = new ClearQuestListBodyBuilder();
            var built = builder.TryBuild(snapshot, 0, out var body);

            var failures = 0;
            Check("builder succeeds", built, ref failures);
            Check("payload length is fixed at 30000",
                body.Length == 4 + ClearQuestListBodyBuilder.PayloadLength
                && BitConverter.ToInt32(body, 0) == ClearQuestListBodyBuilder.PayloadLength,
                ref failures);
            Check("dimension-seal quest flag keeps quest-id offset",
                body[4 + 13502] == 1,
                ref failures);
            Check("last valid quest slot is retained",
                body[4 + 29999] == 7,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
