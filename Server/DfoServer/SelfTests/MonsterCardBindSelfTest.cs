using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;

namespace DfoServer.SelfTests
{
    public static class MonsterCardBindSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MONSTERCARD_BIND selftest ===");
            var failures = 0;
            var config = new MonsterCardBindConfig
            {
                MixProbability = new Dictionary<int, int> { [0] = 40000, [1] = 10000, [2] = 3000, [3] = 0 },
                BinderRates = new Dictionary<int, int> { [0] = 100, [1] = 200, [2] = 300 },
                BindList = new List<MonsterCardBindEntry>
                {
                    new MonsterCardBindEntry { ItemId = 1000, Rarity = 0, Weight = 500 },
                    new MonsterCardBindEntry { ItemId = 1001, Rarity = 0, Weight = 0 },
                    new MonsterCardBindEntry { ItemId = 2000, Rarity = 1, Weight = 100 },
                },
            };

            Check("same white base is 40%", Weight(config, 0, 0, 0) == 40000, ref failures);
            Check("white plus purple cross-tier is 4%", Weight(config, 0, 2, 0) == 4000, ref failures);
            Check("white plus pink cross-tier is 0.12%", Weight(config, 0, 3, 0) == 120, ref failures);
            Check("gold binder triples and caps at 100%", Weight(config, 0, 0, 2) == 100000, ref failures);
            Check("zero-weight cards cannot be selected",
                config.TrySelectResult(0, max => max - 1, out var selected) && selected.ItemId == 1000,
                ref failures);
            Check("success ACK retains verified 19-byte layout", CheckAck(), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static int Weight(MonsterCardBindConfig config, int first, int second, int binder)
            => config.TryCalculateSuccessWeight(first, second, binder, out var value) ? value : -1;

        private static bool CheckAck()
        {
            var result = new MonsterCardBindResult
            {
                ResultItemId = 3752,
                Grant = new InventoryRewardGrantResult { SlotIndex = 246 },
            };
            var body = MonsterCardBindAckBuilder.BuildSuccess(105, 241, 245, result);
            return body.Length == 19
                && BitConverter.ToString(body) == "01-69-00-F1-00-F5-00-01-F6-00-A8-0E-00-00-01-00-00-00-00";
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine(condition ? $"  [PASS] {name}" : $"  [FAIL] {name}");
            if (!condition)
                failures++;
        }
    }
}
