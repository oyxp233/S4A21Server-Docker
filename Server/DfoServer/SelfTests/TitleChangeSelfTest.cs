using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class TitleChangeSelfTest
    {
        private const int NormalThreadItemId = 10007724;
        private const int OrnateThreadItemId = 10007725;
        private const int TargetRateThreadItemId = 10015205;
        private const int LimitedCubeItemId = 2683522;
        private const int AngelTitleItemId = 400330031;
        private const int DevilTitleItemId = 400330032;
        private const int UpgradedAngelTitleItemId = 400330033;
        private const int UpgradedDevilTitleItemId = 400330034;
        private const int TargetRateTitleItemId = 2676151;
        private const int LimitedCubeTargetItemId = 100330789;
        private const int ClearCubeFragmentItemId = 3037;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            TestPacketLayouts();
            TestPvfRules();
            TestRepeatedTitleChange();
            TestLimitedCubeMaterials();
            TestMissingMaterialIsAtomic();
            Console.WriteLine(_failures == 0
                ? "[PASS] title change selftest"
                : $"[FAIL] title change selftest failures={_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestPacketLayouts()
        {
            var titleAck = TitleChangeAckBuilder.BuildSuccess(new InventoryTitleChangeResult
            {
                SourceItemId = NormalThreadItemId,
                ResultItemId = UpgradedAngelTitleItemId,
                IsSuccessBranch = true,
            });
            Check(
                TitleChangeRequestParser.TryParse(
                    new byte[] { 0x76, 0x00, 0x2F, 0x00 },
                    out var titleRequest)
                && titleRequest.SourceSlotIndex == 118
                && titleRequest.TargetSlotIndex == 47
                && titleAck.Length == 10
                && titleAck[0] == 1
                && titleAck[1] == 1
                && BitConverter.ToInt32(titleAck, 2) == UpgradedAngelTitleItemId
                && BitConverter.ToInt32(titleAck, 6) == NormalThreadItemId
                && TitleChangeAckBuilder.BuildError().SequenceEqual(new byte[] { 0, 0x11 }),
                "0x0353 request and ACK match the current-client layout");

            var limitedAck = LimitedCubeAckBuilder.BuildSuccess(new InventoryTitleChangeResult
            {
                ResultItemId = LimitedCubeTargetItemId,
                ResultValue = 1,
                ResultItemKind = ItemCore.KindEquipment,
            });
            Check(
                LimitedCubeUseRequestParser.TryParse(
                    new byte[] { 0x37, 0x00, 0x25, 0xED, 0xFA, 0x05, 0x6C, 0x00 },
                    out var limitedRequest)
                && limitedRequest.TargetSlotIndex == 55
                && limitedRequest.TargetItemId == LimitedCubeTargetItemId
                && limitedRequest.CubeSlotIndex == 108
                && limitedAck.SequenceEqual(new byte[]
                {
                    0x01,
                    0x25, 0xED, 0xFA, 0x05,
                    0x01, 0x00,
                    0x01,
                })
                && LimitedCubeAckBuilder.BuildError().SequenceEqual(new byte[] { 0, 0x11 }),
                "0x0152 request and ACK match the current-client layout");
        }

        private static void TestPvfRules()
        {
            Check(
                InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                    NormalThreadItemId,
                    AngelTitleItemId,
                    _ => 0,
                    out var mutual)
                && mutual.IsSuccessBranch
                && mutual.ResultItemId == DevilTitleItemId
                && InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                    NormalThreadItemId,
                    AngelTitleItemId,
                    _ => 98,
                    out var upgrade)
                && upgrade.ResultItemId == UpgradedAngelTitleItemId
                && InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                    OrnateThreadItemId,
                    UpgradedDevilTitleItemId,
                    _ => 0,
                    out var ornate)
                && !ornate.IsSuccessBranch
                && ornate.ResultItemId == UpgradedAngelTitleItemId,
                "title table preserves mutual, upgrade, and failure branches");

            var mainTable = TitleChangeMainFile.Parse(
                PvfArchiveAccessor.ReadText("etc/aradtitlechange_main.etc"));
            var targetRateEntry = mainTable.Entries
                .FirstOrDefault(entry => entry.SourceItemId == TargetRateThreadItemId);
            var targetRate = targetRateEntry?.Targets
                .FirstOrDefault(target => target.ItemId == TargetRateTitleItemId);
            var effectiveRate = PvfTitleChangeTableRuleProvider.GetEffectiveSuccessRate(
                targetRateEntry,
                targetRate);
            var rateRule = InventoryTitleChangeRule.CreateTitleChange(
                new Dictionary<int, int> { [TargetRateTitleItemId] = effectiveRate },
                new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>
                {
                    [TargetRateTitleItemId] = new[]
                    {
                        new InventoryTitleChangeResultOption(1, 100),
                    },
                },
                new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>
                {
                    [TargetRateTitleItemId] = new[]
                    {
                        new InventoryTitleChangeResultOption(2, 100),
                    },
                });
            Check(
                targetRateEntry?.SuccessRate == 0
                && targetRate?.SuccessRate == 5000
                && rateRule.TrySelectResult(
                    TargetRateTitleItemId,
                    _ => 4999,
                    out _,
                    out var successBranch)
                && successBranch
                && rateRule.TrySelectResult(
                    TargetRateTitleItemId,
                    _ => 5000,
                    out _,
                    out var failureBranch)
                && !failureBranch,
                "target-specific PVF success rate overrides a zero global rate");

            Check(
                InventoryTitleChangeRuleResolver.TryResolveLimitedCube(
                    LimitedCubeItemId,
                    LimitedCubeTargetItemId,
                    out var limited)
                && limited.ResultItemId != LimitedCubeTargetItemId
                && limited.AdditionalMaterials.Count == 1
                && limited.AdditionalMaterials[0].ItemTemplateId == ClearCubeFragmentItemId
                && limited.AdditionalMaterials[0].Count == 10
                && !InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                    LimitedCubeItemId,
                    LimitedCubeTargetItemId,
                    out _),
                "limited-cube PVF is typed and cannot enter the 0x0353 rule path");
        }

        private static void TestRepeatedTitleChange()
        {
            const short sourceSlot = 118;
            const short targetSlot = 47;
            var inventory = new InventoryService(900001, 900001);
            var source = ItemCore.Create(ItemCore.KindConsumable, NormalThreadItemId);
            source.Count = 3;
            var originalTarget = ItemCore.Create(ItemCore.KindEquipment, AngelTitleItemId);
            originalTarget.Value = 777;
            originalTarget.Attr = 0xA5;
            originalTarget.SealFlag = 4;
            originalTarget.EnchantCardId = 123456;
            originalTarget.AmplifyValue = 87;
            inventory.AttachItem(InventoryListType.Main, sourceSlot, source);
            inventory.AttachItem(InventoryListType.Main, targetSlot, originalTarget);

            var first = TryChangeTitle(
                inventory,
                sourceSlot,
                targetSlot,
                NormalThreadItemId,
                AngelTitleItemId,
                out var firstResult);
            InventoryTitleChangeResult secondResult = null;
            var second = first
                && TryChangeTitle(
                    inventory,
                    sourceSlot,
                    targetSlot,
                    NormalThreadItemId,
                    firstResult.ResultItemId,
                    out secondResult);
            var expectedTarget = originalTarget.Copy();
            expectedTarget.ItemId = secondResult?.ResultItemId ?? 0;
            Check(
                second
                && inventory.GetItem(InventoryListType.Main, sourceSlot)?.Count == 1
                && inventory.GetItem(InventoryListType.Main, targetSlot)?.ToBytes()
                    .SequenceEqual(expectedTarget.ToBytes()) == true,
                "a changed title can change again without losing instance fields");
        }

        private static void TestLimitedCubeMaterials()
        {
            var inventory = CreateLimitedCubeInventory(20, out var source, out var target);
            InventoryTitleChangeResult result = null;
            var changed = InventoryTitleChangeRuleResolver.TryResolveLimitedCube(
                    LimitedCubeItemId,
                    LimitedCubeTargetItemId,
                    out var resolution)
                && InventoryTitleChangeService.TryChange(
                    inventory,
                    CreateRequest(source.ItemId, target.ItemId),
                    resolution,
                    out result);

            Check(
                changed
                && result.Success
                && inventory.GetItem(InventoryListType.Main, 108)?.Count == 1
                && inventory.CountMainItem(ClearCubeFragmentItemId) == 10
                && inventory.GetItem(InventoryListType.Main, 55)?.ItemId == result.ResultItemId,
                "limited cube consumes its ticket and typed additional material atomically");
        }

        private static void TestMissingMaterialIsAtomic()
        {
            var inventory = CreateLimitedCubeInventory(9, out var source, out var target);
            var targetBefore = target.Copy();
            InventoryTitleChangeRuleResolver.TryResolveLimitedCube(
                LimitedCubeItemId,
                LimitedCubeTargetItemId,
                out var resolution);

            Check(
                !InventoryTitleChangeService.TryChange(
                    inventory,
                    CreateRequest(source.ItemId, target.ItemId),
                    resolution,
                    out var result)
                && result.Error == InventoryTitleChangeError.InsufficientMaterials
                && inventory.GetItem(InventoryListType.Main, 108)?.Count == 2
                && inventory.CountMainItem(ClearCubeFragmentItemId) == 9
                && inventory.GetItem(InventoryListType.Main, 55)?.ToBytes()
                    .SequenceEqual(targetBefore.ToBytes()) == true,
                "missing additional material does not consume or mutate anything");
        }

        private static bool TryChangeTitle(
            InventoryService inventory,
            short sourceSlot,
            short targetSlot,
            int sourceItemId,
            int targetItemId,
            out InventoryTitleChangeResult result)
        {
            result = null;
            return InventoryTitleChangeRuleResolver.TryResolveTitleChange(
                    sourceItemId,
                    targetItemId,
                    _ => 0,
                    out var resolution)
                && InventoryTitleChangeService.TryChange(
                    inventory,
                    new InventoryTitleChangeRequest
                    {
                        SourceSlotIndex = sourceSlot,
                        TargetSlotIndex = targetSlot,
                        SourceItemId = sourceItemId,
                        TargetItemId = targetItemId,
                    },
                    resolution,
                    out result);
        }

        private static InventoryService CreateLimitedCubeInventory(
            int materialCount,
            out ItemCore source,
            out ItemCore target)
        {
            var inventory = new InventoryService(900002, 900002);
            source = ItemCore.Create(ItemCore.KindConsumable, LimitedCubeItemId);
            source.Count = 2;
            target = ItemCore.Create(ItemCore.KindEquipment, LimitedCubeTargetItemId);
            target.Value = 1234;
            target.Attr = 0x5A;
            inventory.AttachItem(InventoryListType.Main, 108, source);
            inventory.AttachItem(InventoryListType.Main, 55, target);
            InventoryService.TryResolveMainVirtualSlotByItemId(
                ClearCubeFragmentItemId,
                out var materialSlot,
                out _);
            inventory.SetMainVirtualCount(materialSlot, materialCount);
            return inventory;
        }

        private static InventoryTitleChangeRequest CreateRequest(
            int sourceItemId,
            int targetItemId)
        {
            return new InventoryTitleChangeRequest
            {
                SourceSlotIndex = 108,
                TargetSlotIndex = 55,
                SourceItemId = sourceItemId,
                TargetItemId = targetItemId,
            };
        }

        private static void Check(bool condition, string name)
        {
            if (condition)
            {
                Console.WriteLine($"  [PASS] {name}");
                return;
            }

            _failures++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }
}
