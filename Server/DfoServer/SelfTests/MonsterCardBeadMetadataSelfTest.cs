using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.SelfTests
{
    public static class MonsterCardBeadMetadataSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MONSTER_CARD_BEAD_METADATA selftest ===");
            var failures = 0;
            var card = StackableItemFile.Parse(@"
[item category]
    `monster card`
[/item category]
");
            var bead = StackableItemFile.Parse(@"
[monster card id]
    10089629
    10089630
[/monster card id]
[target item id]
    108010296
    108000326
[/target item id]
[bead limited usable item]
    400330047
    400330048
[/bead limited usable item]
");

            Check("item category marks monster card",
                ItemMetadataResolver.IsMonsterCard(card), ref failures);
            Check("monster card id marks monster card bead",
                ItemMetadataResolver.IsMonsterCardBead(bead), ref failures);
            Check("monster card id parses as list",
                bead.MonsterCardIds.Count == 2
                && bead.MonsterCardIds[0] == 10089629
                && bead.MonsterCardIds[1] == 10089630,
                ref failures);
            Check("legacy monster card id keeps first parsed id",
                bead.MonsterCardId == 10089629,
                ref failures);
            Check("target item id parses as list",
                bead.TargetItemIds.Count == 2
                && bead.TargetItemIds[0] == 108010296
                && bead.TargetItemIds[1] == 108000326,
                ref failures);
            Check("bead limited usable item parses as list",
                bead.BeadLimitedUsableItemIds.Count == 2
                && bead.BeadLimitedUsableItemIds[0] == 400330047
                && bead.BeadLimitedUsableItemIds[1] == 400330048,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"  [PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }
}
