using DfoServer.Game.DailyReset;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class DailyRefillItemSelfTest
    {
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== DAILY_REFILL_ITEM selftest ===");

            const string text = @"
[refill item]
    2680738 2 `2030-01-01 06:00:00` 0
    690001556 1 `2030-01-01 06:00:00` 1
    123 0 `2030-01-01 06:00:00` 0
    124 1 `2015-01-01 06:00:00` 0
    125 1 `2030-01-01 06:00:00` 9
[/refill item]";
            var rules = PvfDailyRefillItemProvider.Parse(text, new DateTime(2026, 8, 4, 6, 0, 0));
            Check("valid records parsed", rules.Count == 2);
            var bull = rules.SingleOrDefault(x => x.ItemId == 690001556);
            Check("bull quantity is one", bull?.Quantity == 1);
            Check("bull mode is additive", bull?.Mode == DailyRefillMode.AddUpToStackLimit);

            var target = new DailyRefillItemRule
            {
                ItemId = 1,
                Quantity = 3,
                ExpirationBeijing = DateTime.MaxValue,
                Mode = DailyRefillMode.RefillToTarget,
            };
            Check("target 0 to 3", DailyRefillItemPolicy.CalculateGrant(target, 0, 10) == 3);
            Check("target 2 to 3", DailyRefillItemPolicy.CalculateGrant(target, 2, 10) == 1);
            Check("target at cap", DailyRefillItemPolicy.CalculateGrant(target, 3, 10) == 0);
            Check("target above cap", DailyRefillItemPolicy.CalculateGrant(target, 5, 10) == 0);

            var additive = new DailyRefillItemRule
            {
                ItemId = 2,
                Quantity = 1,
                ExpirationBeijing = DateTime.MaxValue,
                Mode = DailyRefillMode.AddUpToStackLimit,
            };
            Check("additive 0 to 1", DailyRefillItemPolicy.CalculateGrant(additive, 0, 10) == 1);
            Check("additive 9 to 10", DailyRefillItemPolicy.CalculateGrant(additive, 9, 10) == 1);
            Check("additive at limit", DailyRefillItemPolicy.CalculateGrant(additive, 10, 10) == 0);

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }
    }
}
