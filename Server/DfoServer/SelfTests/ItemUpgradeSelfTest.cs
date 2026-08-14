using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using System;

namespace DfoServer.SelfTests
{
    public static class ItemUpgradeSelfTest
    {
        private const int TargetItemId = 0x00006B8B;
        private const short TargetSlot = 10;
        private const short AdvancedMaterialSlot = 149;
        private const short ClearCubeSlot = 358;
        private const int InitialMaterialCount = 1000;
        private const int InitialGold = 500_000_000;
        private const int CurrentUpgradeLevel = 12;
        private const int UpgradeProbabilityTitleItemId = 26699;
        private static int _failures;
        private static UpgradeTableRow _advancedRow;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== ITEM_UPGRADE selftest ===");
            TestProtocol();
            TestCurrentPvf();
            TestUpgradeProbabilityTitle();
            TestNpcUpgradeTransactions();
            TestOutworldVigorRejection();
            Console.WriteLine(_failures == 0
                ? "ItemUpgradeSelfTest OK"
                : $"ItemUpgradeSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestProtocol()
        {
            var advancedCapture = Hex(
                "02-00-0A-00-8B-6B-00-00-95-00-FF-FF-12-00-00-00-" +
                "E8-A3-81-E5-86-B3-E5-88-83-20-2D-20-E5-9B-BD-E6-AE-87");
            Check(ItemUpgradeRequest.TryParse(advancedCapture, out var request)
                && request.Method == ItemUpgradeMethod.AdvancedReinforce
                && request.Mode == ItemUpgradeMode.Reinforce
                && request.TargetSlotIndex == TargetSlot
                && request.TargetItemTemplateId == TargetItemId
                && request.MaterialSlotIndex == AdvancedMaterialSlot
                && request.OptionalTicketSlotIndex == -1
                && request.TargetItemName == "裁决刃 - 国殇",
                "captured advanced-reinforcement request");

            var ordinaryCapture = (byte[])advancedCapture.Clone();
            ordinaryCapture[0] = 0;
            ordinaryCapture[8] = 0x66;
            ordinaryCapture[9] = 0x01;
            Check(ItemUpgradeRequest.TryParse(ordinaryCapture, out var ordinary)
                && ordinary.Method == ItemUpgradeMethod.Reinforce
                && ordinary.Mode == ItemUpgradeMode.Reinforce
                && ordinary.MaterialSlotIndex == ClearCubeSlot,
                "captured ordinary-reinforcement request remains supported");

            var unsupported = (byte[])advancedCapture.Clone();
            unsupported[0] = 3;
            Check(!ItemUpgradeRequest.TryParse(unsupported, out _),
                "unknown reinforcement method is rejected");

            var ack = ItemUpgradeAckBuilder.BuildSuccess(new ItemUpgradeResult
            {
                Method = ItemUpgradeMethod.AdvancedReinforce,
                Mode = ItemUpgradeMode.Reinforce,
                MaterialSlotIndex = AdvancedMaterialSlot,
                MaterialRemainingStackCount = 840,
                OptionalTicketSlotIndex = -1,
                OldLevel = 12,
                ResultCode = 0,
                NewLevel = 13,
                TargetSlotIndex = TargetSlot,
            });
            Check(Convert.ToHexString(ack) == "0102950048030000FFFF000C000D000A00FFFF",
                "success ACK echoes advanced method 2");
        }

        private static void TestCurrentPvf()
        {
            Check(ItemUpgradeTableProvider.TryGetRow(
                    ItemUpgradeTableKind.Normal,
                    CurrentUpgradeLevel + 1,
                    out var row)
                && row.MaterialItemId == 3037
                && row.MaterialCount == 160
                && row.DerivedSuccessWeight == 18000,
                "current reinforcement table level-13 inputs");

            Check(ItemUpgradeTableProvider.TryGetRow(
                    ItemUpgradeTableKind.Advanced,
                    CurrentUpgradeLevel + 1,
                    out _advancedRow)
                && _advancedRow.MaterialItemId > 0
                && _advancedRow.MaterialCount > 0
                && _advancedRow.DerivedSuccessWeight > 0
                && ItemMetadataResolver.TryLoadStackableFile(
                    _advancedRow.MaterialItemId,
                    out var advancedMaterial)
                && advancedMaterial.Name == "高级炉岩炭",
                "current PVF defines structured advanced-reinforcement inputs");
        }

        private static void TestUpgradeProbabilityTitle()
        {
            var parsed = EquipmentFile.Parse(@"
[equipment type]
    `[title name]` 1
[upgrade prob increase]
    1000000
");
            Check(parsed.UpgradeProbabilityIncrease == 1000000,
                "equipment parser reads upgrade-probability increase");

            Check(ItemMetadataResolver.TryLoadEquipmentFile(
                    UpgradeProbabilityTitleItemId,
                    out var title)
                && title.UpgradeProbabilityIncrease > 0,
                "current Kelly title defines an upgrade-probability bonus");

            if (title == null || title.UpgradeProbabilityIncrease <= 0)
                return;

            var titleBonus = title.UpgradeProbabilityIncrease;

            var reinforceInventory = CreateInventory();
            EquipUpgradeProbabilityTitle(reinforceInventory);
            reinforceInventory.SetMainVirtualCount(ClearCubeSlot, 3037, InitialMaterialCount);
            Check(InventoryItemUpgradeService.TryUpgradeItem(
                    reinforceInventory,
                    CreateCommand(ItemUpgradeMethod.Reinforce, ClearCubeSlot),
                    out var reinforce)
                && reinforce.FinalSuccessWeight
                    == Math.Min(100000, 18000 + titleBonus),
                "equipped title increases reinforcement success weight");

            if (!ItemUpgradeTableProvider.TryGetRow(
                    ItemUpgradeTableKind.Amplify,
                    CurrentUpgradeLevel + 1,
                    out var amplifyRow))
            {
                Check(false, "amplification title test requires a PVF table row");
                return;
            }

            var amplifyInventory = CreateInventory();
            var amplifiedTarget = amplifyInventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            amplifiedTarget.AmplifyType = (byte)AmplifyAttributeType.Strength;
            amplifiedTarget.AmplifyValue = 1;
            amplifyInventory.SetItem(InventoryListType.Main, TargetSlot, amplifiedTarget);
            EquipUpgradeProbabilityTitle(amplifyInventory);

            var amplifyMaterialSlot = AddUpgradeMaterial(amplifyInventory, amplifyRow);
            var amplifyCommand = CreateCommand(ItemUpgradeMethod.Amplify, amplifyMaterialSlot);
            amplifyCommand.Mode = ItemUpgradeMode.Amplify;
            Check(InventoryItemUpgradeService.TryUpgradeItem(
                    amplifyInventory,
                    amplifyCommand,
                    out var amplify)
                && amplify.FinalSuccessWeight
                    == Math.Min(100000, amplifyRow.DerivedSuccessWeight + titleBonus),
                "equipped title increases amplification success weight");
        }

        private static void TestNpcUpgradeTransactions()
        {
            if (_advancedRow == null)
            {
                Check(false, "advanced NPC reinforcement requires a PVF table row");
                return;
            }

            var ordinaryInventory = CreateInventory();
            ordinaryInventory.SetMainVirtualCount(ClearCubeSlot, 3037, InitialMaterialCount);
            Check(InventoryItemUpgradeService.TryUpgradeItem(
                    ordinaryInventory,
                    CreateCommand(ItemUpgradeMethod.Reinforce, ClearCubeSlot),
                    out var ordinary)
                && ordinary.Scene == ItemUpgradeScene.Npc
                && ordinary.Method == ItemUpgradeMethod.Reinforce
                && ordinary.MaterialItemTemplateId == 3037
                && ordinary.MaterialRemainingStackCount == 840
                && ordinary.FinalSuccessWeight == 18000
                && ordinaryInventory.GetMainVirtualCount(ClearCubeSlot)?.Count == 840,
                "ordinary NPC reinforcement still consumes clear cubes at base rate");

            var advancedInventory = CreateInventory();
            AddAdvancedMaterial(advancedInventory, _advancedRow.MaterialItemId, InitialMaterialCount);
            Check(InventoryItemUpgradeService.TryUpgradeItem(
                    advancedInventory,
                    CreateCommand(ItemUpgradeMethod.AdvancedReinforce, AdvancedMaterialSlot),
                    out var advanced)
                && advanced.Scene == ItemUpgradeScene.Npc
                && advanced.Method == ItemUpgradeMethod.AdvancedReinforce
                && advanced.Mode == ItemUpgradeMode.Reinforce
                && advanced.MaterialItemTemplateId == _advancedRow.MaterialItemId
                && advanced.MaterialRemainingStackCount == InitialMaterialCount - _advancedRow.MaterialCount
                && advanced.FinalSuccessWeight == _advancedRow.DerivedSuccessWeight
                && advancedInventory.GetItem(
                    InventoryListType.Main,
                    AdvancedMaterialSlot)?.Count == InitialMaterialCount - _advancedRow.MaterialCount,
                "advanced NPC reinforcement uses its PVF material and success weight");

            var invalidCombinationInventory = CreateInventory();
            AddAdvancedMaterial(
                invalidCombinationInventory,
                _advancedRow.MaterialItemId,
                InitialMaterialCount);
            var invalidCombinationCommand = CreateCommand(
                ItemUpgradeMethod.AdvancedReinforce,
                AdvancedMaterialSlot);
            invalidCombinationCommand.Mode = ItemUpgradeMode.Amplify;
            var invalidCombinationTargetBefore = GetTargetBytes(invalidCombinationInventory);
            invalidCombinationInventory.ClearDirtyState();
            Check(!InventoryItemUpgradeService.TryUpgradeItem(
                    invalidCombinationInventory,
                    invalidCombinationCommand,
                    out var invalidCombination)
                && invalidCombination.ErrorCode == ItemUpgradeResult.ErrorWrongUpgradeMode
                && GetTargetBytes(invalidCombinationInventory) == invalidCombinationTargetBefore
                && invalidCombinationInventory.GetItem(
                    InventoryListType.Main,
                    AdvancedMaterialSlot)?.Count == InitialMaterialCount
                && invalidCombinationInventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count == InitialGold
                && invalidCombinationInventory.GetDirtySlots(InventoryListType.Main).Count == 0
                && invalidCombinationInventory.DirtyMainVirtualCountSlots.Count == 0,
                "inconsistent advanced method and mode reject without mutation");

            var insufficientInventory = CreateInventory();
            var insufficientMaterialCount = _advancedRow.MaterialCount - 1;
            AddAdvancedMaterial(
                insufficientInventory,
                _advancedRow.MaterialItemId,
                insufficientMaterialCount);
            Check(!InventoryItemUpgradeService.TryUpgradeItem(
                    insufficientInventory,
                    CreateCommand(ItemUpgradeMethod.AdvancedReinforce, AdvancedMaterialSlot),
                    out var insufficient)
                && insufficient.ErrorCode == ItemUpgradeResult.ErrorInvalidMaterial
                && insufficientInventory.GetItem(
                    InventoryListType.Main,
                    AdvancedMaterialSlot)?.Count == insufficientMaterialCount
                && insufficientInventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count == InitialGold
                && insufficientInventory.GetItem(
                    InventoryListType.Main,
                    TargetSlot)?.Upgrade == CurrentUpgradeLevel,
                "insufficient high-grade carbon rejects without mutation");
        }

        private static void TestOutworldVigorRejection()
        {
            if (_advancedRow == null)
            {
                Check(false, "outworld-vigor rejection requires an advanced PVF table row");
                return;
            }

            var ordinaryInventory = CreateInventory();
            ordinaryInventory.SetMainVirtualCount(ClearCubeSlot, 3037, InitialMaterialCount);
            SetUnidentifiedOutworldVigor(ordinaryInventory);
            var ordinaryTargetBefore = GetTargetBytes(ordinaryInventory);
            ordinaryInventory.ClearDirtyState();
            Check(!InventoryItemUpgradeService.TryUpgradeItem(
                    ordinaryInventory,
                    CreateCommand(ItemUpgradeMethod.Reinforce, ClearCubeSlot),
                    out var ordinary)
                && IsOutworldVigorRejection(ordinary)
                && GetTargetBytes(ordinaryInventory) == ordinaryTargetBefore
                && ordinaryInventory.GetMainVirtualCount(ClearCubeSlot)?.Count == InitialMaterialCount
                && ordinaryInventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count == InitialGold
                && ordinaryInventory.GetDirtySlots(InventoryListType.Main).Count == 0
                && ordinaryInventory.DirtyMainVirtualCountSlots.Count == 0,
                "ordinary NPC reinforcement rejects unidentified outworld vigor without mutation");

            var advancedInventory = CreateInventory();
            AddAdvancedMaterial(advancedInventory, _advancedRow.MaterialItemId, InitialMaterialCount);
            SetUnidentifiedOutworldVigor(advancedInventory);
            var advancedTargetBefore = GetTargetBytes(advancedInventory);
            advancedInventory.ClearDirtyState();
            Check(!InventoryItemUpgradeService.TryUpgradeItem(
                    advancedInventory,
                    CreateCommand(ItemUpgradeMethod.AdvancedReinforce, AdvancedMaterialSlot),
                    out var advanced)
                && IsOutworldVigorRejection(advanced)
                && GetTargetBytes(advancedInventory) == advancedTargetBefore
                && advancedInventory.GetItem(
                    InventoryListType.Main,
                    AdvancedMaterialSlot)?.Count == InitialMaterialCount
                && advancedInventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count == InitialGold
                && advancedInventory.GetDirtySlots(InventoryListType.Main).Count == 0
                && advancedInventory.DirtyMainVirtualCountSlots.Count == 0,
                "advanced NPC reinforcement rejects unidentified outworld vigor without mutation");
        }

        private static bool IsOutworldVigorRejection(ItemUpgradeResult result)
        {
            return result != null
                && !result.Success
                && result.ErrorCode == ItemUpgradeResult.ErrorWrongUpgradeMode
                && result.MainRefreshSlots.Count == 0;
        }

        private static void SetUnidentifiedOutworldVigor(InventoryService inventory)
        {
            var target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.AmplifyType = 0x80;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
        }

        private static string GetTargetBytes(InventoryService inventory)
        {
            return Convert.ToHexString(
                inventory.GetItem(InventoryListType.Main, TargetSlot).ToBytes());
        }

        private static InventoryService CreateInventory()
        {
            var inventory = new InventoryService(991200, 991200);
            var metadata = ItemMetadataResolver.Resolve(TargetItemId);
            inventory.SetItem(InventoryListType.Main, TargetSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = TargetItemId,
                Uid = 1,
                Durability = metadata.Durability,
                Upgrade = CurrentUpgradeLevel,
            });
            inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                InitialGold);
            inventory.ClearDirtyState();
            return inventory;
        }

        private static void AddAdvancedMaterial(InventoryService inventory, int itemId, int count)
        {
            inventory.SetItem(InventoryListType.Main, AdvancedMaterialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindMaterial,
                ItemId = itemId,
                Count = count,
            });
            inventory.ClearDirtyState();
        }

        private static void EquipUpgradeProbabilityTitle(InventoryService inventory)
        {
            inventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.TitleName,
                new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = UpgradeProbabilityTitleItemId,
                    Uid = 2,
                });
            inventory.ClearDirtyState();
        }

        private static short AddUpgradeMaterial(InventoryService inventory, UpgradeTableRow row)
        {
            if (CurrencyService.IsCubeFragment(row.MaterialItemId))
            {
                var slot = (short)CurrencyService.GetCubeFragmentSlot(row.MaterialItemId);
                inventory.SetMainVirtualCount(slot, row.MaterialItemId, InitialMaterialCount);
                inventory.ClearDirtyState();
                return slot;
            }

            const short slotIndex = AdvancedMaterialSlot;
            AddAdvancedMaterial(inventory, row.MaterialItemId, InitialMaterialCount);
            return slotIndex;
        }

        private static ItemUpgradeCommand CreateCommand(
            ItemUpgradeMethod method,
            short materialSlotIndex)
        {
            return new ItemUpgradeCommand
            {
                Method = method,
                Mode = ItemUpgradeMode.Reinforce,
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId,
                MaterialSlotIndex = materialSlotIndex,
                OptionalTicketSlotIndex = -1,
                TargetItemName = "裁决刃 - 国殇",
            };
        }

        private static byte[] Hex(string value)
            => Convert.FromHexString(value.Replace("-", string.Empty));

        private static void Check(bool condition, string name)
        {
            Console.WriteLine(condition ? $"  [PASS] {name}" : $"  [FAIL] {name}");
            if (!condition)
                _failures++;
        }
    }
}
