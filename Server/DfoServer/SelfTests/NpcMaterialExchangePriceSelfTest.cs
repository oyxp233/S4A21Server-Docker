using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.SelfTests
{
    // Keeps the PVF price semantics used by ordinary NPC material exchanges
    // independent from the live Script.pvf contents.
    public static class NpcMaterialExchangePriceSelfTest
    {
        private static int _fail;

        public static int Run()
        {
            _fail = 0;
            Console.WriteLine("=== NPC_MATERIAL_EXCHANGE_PRICE selftest ===");

            Check("price plus material keeps gold price",
                ItemMetadataResolver.ResolveBuyGold(50000, 0) == 50000);
            Check("matching negative add price makes exchange material-only",
                ItemMetadataResolver.ResolveBuyGold(50000, -50000) == 0);
            Check("partial negative add price reduces gold price",
                ItemMetadataResolver.ResolveBuyGold(50000, -10000) == 40000);
            Check("missing price is material-only regardless of value",
                ItemMetadataResolver.ResolveBuyGold(-1, 0) == 0);

            var equipment = EquipmentFile.Parse(@"
[price]
50000
[add price]
-50000
[value]
85120
[need material]
10088692 230");
            Check("equipment parses signed add price", equipment.AddPrice == -50000);
            Check("equipment parses material cost", equipment.NeedMaterial == "10088692 230");
            Check("equipment effective exchange gold is zero",
                ItemMetadataResolver.ResolveBuyGold(equipment.Price, equipment.AddPrice) == 0);

            var stackable = StackableItemFile.Parse(@"
[price]
50000
[add price]
-10000
[need material]
10088692 230");
            Check("stackable parses signed add price", stackable.AddPrice == -10000);
            Check("stackable effective exchange gold is adjusted",
                ItemMetadataResolver.ResolveBuyGold(stackable.Price, stackable.AddPrice) == 40000);

            VerifyMaterialExchangeAckCount();
            VerifyNeedMaterialIsNotUseCost();
            VerifyGoldPurchaseAckCount();

            Console.WriteLine($"=== SUMMARY: fail={_fail} ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void VerifyMaterialExchangeAckCount()
        {
            const int characterId = 947001;
            const int accountId = 947000;
            const int whiteCubeItemId = 3034;
            const int existingTargetCount = 7;
            const int buyCount = 1;

            var metadata = ItemMetadataResolver.Resolve(whiteCubeItemId);
            Check("white cube is material exchange fixture", metadata.IsMaterialExchange);

            var inventory = new InventoryService(characterId, accountId);
            inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 1_000_000);
            InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                metadata.NeedMaterialId,
                ItemCreateReason.NpcShopPurchase,
                metadata.NeedMaterialCount * buyCount,
                out _);
            InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                whiteCubeItemId,
                ItemCreateReason.NpcShopPurchase,
                existingTargetCount,
                out _);

            var ok = InventoryShopRuntimeService.TryBuyNpcItem(
                inventory,
                whiteCubeItemId,
                buyCount,
                out var result);

            Check("material-exchange purchase succeeds", ok && result != null);
            Check("material-exchange ACK uses purchased count, not final stack",
                result != null
                && result.InstanceValue == buyCount
                && result.RemainingStackCount == buyCount);
            Check("material-exchange online target stack still accumulates",
                inventory.CountMainItem(whiteCubeItemId) == existingTargetCount + buyCount);
            Check("material-exchange cost material is consumed",
                result != null
                && result.CostItemTemplateId == metadata.NeedMaterialId
                && inventory.CountMainItem(metadata.NeedMaterialId) == 0);
        }

        private static void VerifyGoldPurchaseAckCount()
        {
            const int characterId = 947011;
            const int accountId = 947010;
            const int itemId = 1004;
            const int existingTargetCount = 3;
            const int buyCount = 2;

            var metadata = ItemMetadataResolver.Resolve(itemId);
            Check("gold-purchase fixture is ordinary stackable", metadata.IsStackable && !metadata.IsMaterialExchange);

            var inventory = new InventoryService(characterId, accountId);
            inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 1_000_000);
            InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                itemId,
                ItemCreateReason.NpcShopPurchase,
                existingTargetCount,
                out _);

            var ok = InventoryShopRuntimeService.TryBuyNpcItem(
                inventory,
                itemId,
                buyCount,
                out var result);

            Check("gold-purchase stackable succeeds", ok && result != null);
            Check("gold-purchase ACK uses purchased count, not final stack",
                result != null
                && result.InstanceValue == buyCount
                && result.RemainingStackCount == buyCount);
            Check("gold-purchase online stack still accumulates",
                inventory.CountMainItem(itemId) == existingTargetCount + buyCount);
        }

        private static void VerifyNeedMaterialIsNotUseCost()
        {
            var stackable = StackableItemFile.Parse(@"
[stackable type]
`[booster]` 0
[need material]
10088692 230");

            InventoryPackageRewardResolver.ResolveNeedMaterial(
                10000001,
                stackable,
                out var materialItemTemplateId,
                out var materialCountPerUse);

            Check("need material is acquisition cost only",
                materialItemTemplateId == 0 && materialCountPerUse == 0);

            var randomBox = StackableItemFile.Parse(@"
[RANDOMBOX]
    [sealing removal item]
    10088692 2
    [/sealing removal item]
[/RANDOMBOX]");

            InventoryPackageRewardResolver.ResolveNeedMaterial(
                10000001,
                randomBox,
                out materialItemTemplateId,
                out materialCountPerUse);

            Check("sealing removal item is use cost",
                materialItemTemplateId == 10088692 && materialCountPerUse == 2);
        }

        private static void Check(string label, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}");
            if (!passed)
                _fail++;
        }
    }
}
