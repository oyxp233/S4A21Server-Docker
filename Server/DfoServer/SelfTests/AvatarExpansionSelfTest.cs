using DfoServer.Game.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class AvatarExpansionSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== AVATAR_EXPANSION selftest ===");
            var failures = 0;

            Check(
                "avatar expansion rule maps stage 1 item",
                AvatarInventoryExpansionRule.TryResolveStage(0x0028F3FD, out var stage1, out var param1)
                && stage1 == 1
                && param1 == 7,
                ref failures);
            Check(
                "avatar expansion rule maps stage 15 item",
                AvatarInventoryExpansionRule.TryResolveStage(0x0028F40B, out var stage15, out var param15)
                && stage15 == 15
                && param15 == 105,
                ref failures);
            Check(
                "avatar expansion rule rejects out-of-range items",
                !AvatarInventoryExpansionRule.TryResolveStage(0x0028F3FC, out _, out _)
                && !AvatarInventoryExpansionRule.TryResolveStage(0x0028F40C, out _, out _),
                ref failures);
            Check(
                "avatar expansion param16 clamps to 0..105",
                AvatarInventoryExpansionRule.NormalizeParam16(-5) == 0
                && AvatarInventoryExpansionRule.NormalizeParam16(0) == 0
                && AvatarInventoryExpansionRule.NormalizeParam16(105) == 105
                && AvatarInventoryExpansionRule.NormalizeParam16(106) == 105
                && AvatarInventoryExpansionRule.NormalizeParam16(9999) == 105,
                ref failures);

            Check(
                "avatar open range is base 105 slots without expansion",
                RangeIs(ItemSlotBoundService.GetAvatarOpenRange(0), 0, 104),
                ref failures);
            Check(
                "avatar open range grows 7 slots per stage",
                RangeIs(ItemSlotBoundService.GetAvatarOpenRange(7), 0, 111)
                && RangeIs(ItemSlotBoundService.GetAvatarOpenRange(70), 0, 174)
                && RangeIs(ItemSlotBoundService.GetAvatarOpenRange(105), 0, 209),
                ref failures);
            Check(
                "avatar open range never exceeds physical 0..209",
                RangeIs(ItemSlotBoundService.GetAvatarOpenRange(999), 0, 209),
                ref failures);
            Check(
                "avatar physical range stays 0..209 regardless of expansion",
                ItemSlotBoundService.TryGetItemSpacePhysicalRange(
                    InventoryListType.Avatar,
                    out var physicalRange)
                && RangeIs(physicalRange, 0, 209),
                ref failures);

            Check(
                "slot 104 is avatar-kind at param16=0 but slot 105 is not",
                ItemSlotBoundService.TryGetItemKindBySlot(
                    InventoryListType.Avatar, 104, ItemSlotBoundService.MainExpandStageFull, out var kind104, 0)
                && kind104 == ItemCore.KindAvatar
                && !ItemSlotBoundService.TryGetItemKindBySlot(
                    InventoryListType.Avatar, 105, ItemSlotBoundService.MainExpandStageFull, out _, 0),
                ref failures);
            Check(
                "slot 105 becomes avatar-kind after stage 1 expansion",
                ItemSlotBoundService.TryGetItemKindBySlot(
                    InventoryListType.Avatar, 105, ItemSlotBoundService.MainExpandStageFull, out var kind105, 7)
                && kind105 == ItemCore.KindAvatar,
                ref failures);
            Check(
                "avatar slot validity honors expansion param",
                ItemSlotBoundService.IsValidSlotForKind(
                    ItemCore.KindAvatar, InventoryListType.Avatar, 150, ItemSlotBoundService.MainExpandStageFull, 70)
                && !ItemSlotBoundService.IsValidSlotForKind(
                    ItemCore.KindAvatar, InventoryListType.Avatar, 150, ItemSlotBoundService.MainExpandStageFull, 7),
                ref failures);

            var inventory = new InventoryService(91031, 91030);
            Check(
                "cerashop probe allows stage-1 expansion purchase at param16=0",
                InventoryCeraShopRuntimeService.TryProbeCeraShopPurchaseEffect(
                    inventory, 102644, out var probeFailure0),
                ref failures);
            inventory.SetListParam16(InventoryListType.Avatar, 7);
            Check(
                "cerashop probe rejects duplicate stage-1 purchase with NoEffect",
                !InventoryCeraShopRuntimeService.TryProbeCeraShopPurchaseEffect(
                    inventory, 102644, out var probeFailure1)
                && probeFailure1 == CeraShopPurchaseFailure.NoEffect,
                ref failures);
            Check(
                "cerashop probe allows higher-stage purchase after stage 1",
                InventoryCeraShopRuntimeService.TryProbeCeraShopPurchaseEffect(
                    inventory, 102645, out _),
                ref failures);
            inventory.SetListParam16(InventoryListType.Avatar, 105);
            Check(
                "cerashop probe rejects any purchase at max expansion",
                !InventoryCeraShopRuntimeService.TryProbeCeraShopPurchaseEffect(
                    inventory, 102658, out var probeFailureMax)
                && probeFailureMax == CeraShopPurchaseFailure.NoEffect,
                ref failures);

            Console.WriteLine(failures == 0
                ? "AVATAR_EXPANSION selftest passed."
                : $"AVATAR_EXPANSION selftest FAILED: {failures} failure(s).");
            return failures;
        }

        private static bool RangeIs(ItemSlotRange range, int start, int end)
            => range.Start == start && range.End == end;

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
