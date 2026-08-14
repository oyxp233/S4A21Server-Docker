using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;

namespace DfoServer.SelfTests
{
    public static class MonsterCardUpgradeSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== UPGRADE_CARD selftest ===");
            var failures = 0;
            var config = MonsterCardUpgradeConfigProvider.Parse(@"
[monster card upgrade calculate const]
10
[card upgrade cost]
5000
");

            Check("white same-rarity chance is 80%", config.CalculateChance(0, 0) == 80000, ref failures);
            Check("blue same-rarity chance is 70%", config.CalculateChance(1, 1) == 70000, ref failures);
            Check("purple same-rarity chance is 60%", config.CalculateChance(2, 2) == 60000, ref failures);
            Check("pink same-rarity chance is 50%", config.CalculateChance(3, 3) == 50000, ref failures);
            Check("same item is guaranteed", config.CalculateChance(3, 3, sameItem: true) == 100000, ref failures);
            Check("higher-rarity material is guaranteed", config.CalculateChance(2, 3) == 100000, ref failures);
            Check("one tier lower halves chance", config.CalculateChance(3, 2) == 25000, ref failures);
            Check("three tiers lower yields 6.25%", config.CalculateChance(3, 0) == 6250, ref failures);
            Check("PVF card upgrade cost is parsed", config.GoldCost == 5000, ref failures);
            Check("same upgrade count can share a stack", CheckCardStackCompatibility(1, 1), ref failures);
            Check("different upgrade counts cannot share a stack", !CheckCardStackCompatibility(0, 1), ref failures);
            Check("success ACK uses verified seven-byte layout", CheckAck(), ref failures);
            Check("same-slot pair upgrades one surviving card", CheckSameSlotPair(config), ref failures);
            Check("same-slot stack splits one upgraded card", CheckSameSlotStack(config), ref failures);
            Check("different-card failure consumes only one material", CheckDifferentCardFailure(config), ref failures);
            Check("stacked target upgrades only one card", CheckStackedTarget(config), ref failures);
            Check("insufficient gold preserves inventory", CheckInsufficientGold(config), ref failures);
            Check("full inventory rejects before charging", CheckFullInventory(config), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool CheckCardStackCompatibility(byte firstUpgrade, byte secondUpgrade)
        {
            var first = ItemCore.Create(ItemCore.KindExpertJobMaterial, 3619);
            first.Count = 1;
            first.EnchantUpgradeCount = firstUpgrade;
            var second = first.Copy();
            second.EnchantUpgradeCount = secondUpgrade;
            return InventoryStackRuleService.CanShareStack(first, second);
        }

        private static bool CheckAck()
        {
            var body = MonsterCardUpgradeAckBuilder.BuildSuccess(new MonsterCardUpgradeResult
            {
                TargetSlot = 235,
                TargetItemId = 0x00000E45,
                MaterialSlot = 244,
                Success = true,
                UpgradeCount = 1,
            });
            return body.Length == 7
                && BitConverter.ToString(body) == "01-01-45-0E-00-00-01";
        }

        private static bool CheckSameSlotPair(MonsterCardUpgradeConfig config)
        {
            var inventory = CreateInventory(2);
            var service = new MonsterCardUpgradeService(config, _ => 99999);
            return service.TryUpgrade(inventory, InventoryListType.Main, 10, 10, 1, out var result, out _)
                && result.Success
                && result.Chance == MonsterCardUpgradeConfig.ProbabilityDenominator
                && result.ResultSlot == 10
                && IsCard(inventory, 10, 1, 1)
                && Gold(inventory) == 5000;
        }

        private static bool CheckSameSlotStack(MonsterCardUpgradeConfig config)
        {
            var inventory = CreateInventory(3);
            var service = new MonsterCardUpgradeService(config, _ => 0);
            return service.TryUpgrade(inventory, InventoryListType.Main, 10, 10, 1, out var result, out _)
                && result.Success
                && result.ResultSlot != 10
                && IsCard(inventory, 10, 1, 0)
                && IsCard(inventory, result.ResultSlot, 1, 1)
                && Gold(inventory) == 5000;
        }

        private static bool CheckDifferentCardFailure(MonsterCardUpgradeConfig config)
        {
            var inventory = CreateInventory(1);
            var material = ItemCore.Create(ItemCore.KindExpertJobMaterial, 3620);
            material.Count = 2;
            inventory.SetItem(InventoryListType.Main, 11, material);
            var service = new MonsterCardUpgradeService(config, _ => 99999);
            var upgraded = service.TryUpgrade(
                inventory, InventoryListType.Main, 10, 11, 1, out var result, out _);
            var remainingMaterial = inventory.GetItem(InventoryListType.Main, 11);
            return upgraded
                && !result.Success
                && IsCard(inventory, 10, 1, 0)
                && remainingMaterial != null
                && remainingMaterial.ItemId == 3620
                && remainingMaterial.Count == 1
                && Gold(inventory) == 5000;
        }

        private static bool CheckStackedTarget(MonsterCardUpgradeConfig config)
        {
            var inventory = CreateInventory(3);
            inventory.SetItem(InventoryListType.Main, 11, CreateCard(1));
            var service = new MonsterCardUpgradeService(config, _ => 0);
            return service.TryUpgrade(inventory, InventoryListType.Main, 10, 11, 1, out var result, out _)
                && result.Success
                && IsCard(inventory, 10, 2, 0)
                && inventory.GetItem(InventoryListType.Main, 11) == null
                && IsCard(inventory, result.ResultSlot, 1, 1);
        }

        private static bool CheckInsufficientGold(MonsterCardUpgradeConfig config)
        {
            var inventory = CreateInventory(2, 4999);
            var service = new MonsterCardUpgradeService(config, _ => 0);
            return !service.TryUpgrade(inventory, InventoryListType.Main, 10, 10, 1, out _, out var rejection)
                && rejection == "insufficient gold"
                && IsCard(inventory, 10, 2, 0)
                && Gold(inventory) == 4999;
        }

        private static bool CheckFullInventory(MonsterCardUpgradeConfig config)
        {
            var inventory = CreateInventory(3);
            for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
            {
                if (slot == 10)
                    continue;
                inventory.SetItem(InventoryListType.Main, slot, new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = 100000000 + slot,
                    Count = 1,
                });
            }
            var service = new MonsterCardUpgradeService(config, _ => 0);
            return !service.TryUpgrade(inventory, InventoryListType.Main, 10, 10, 1, out _, out var rejection)
                && rejection == "inventory full"
                && IsCard(inventory, 10, 3, 0)
                && Gold(inventory) == 10000;
        }

        private static InventoryService CreateInventory(int cardCount, int gold = 10000)
        {
            var inventory = new InventoryService(991100 + cardCount, 991100);
            inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, gold);
            inventory.SetItem(InventoryListType.Main, 10, CreateCard(cardCount));
            return inventory;
        }

        private static ItemCore CreateCard(int count)
        {
            var card = ItemCore.Create(ItemCore.KindExpertJobMaterial, 3619);
            card.Count = count;
            return card;
        }

        private static bool IsCard(InventoryService inventory, short slot, int count, byte upgrade)
        {
            var card = inventory.GetItem(InventoryListType.Main, slot);
            return card != null
                && card.ItemId == 3619
                && card.Count == count
                && card.EnchantUpgradeCount == upgrade;
        }

        private static int Gold(InventoryService inventory)
            => inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine(condition ? $"  [PASS] {name}" : $"  [FAIL] {name}");
            if (!condition)
                failures++;
        }
    }
}
