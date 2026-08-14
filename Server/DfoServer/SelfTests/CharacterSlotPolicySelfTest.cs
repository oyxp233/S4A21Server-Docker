using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.SelfTests
{
    public static class CharacterSlotPolicySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_SLOT_POLICY selftest ===");

            var failures = 0;

            Check("32-slot template resolves to 32",
                CharacterSlotPolicy.ResolveSlotLimit(32, 32) == 32,
                ref failures);

            Check("32-slot template allows 17th character",
                CharacterSlotPolicy.HasAvailableSlot(16, 32, 32),
                ref failures);

            Check("32-slot template blocks 33rd character",
                !CharacterSlotPolicy.HasAvailableSlot(32, 32, 32),
                ref failures);

            Check("secondary slot field is fallback",
                CharacterSlotPolicy.ResolveSlotLimit(0, 24) == 24,
                ref failures);

            Check("default matches roster fallback",
                CharacterSlotPolicy.ResolveSlotLimit(0, 0) == CharacterSlotPolicy.DefaultSlotLimit,
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
