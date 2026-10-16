using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using PvfLib;
using System;

namespace DfoServer.SelfTests
{
    public static class PortableForgeSeparateUpgradeSelfTest
    {
        // 便携式锻造炉（[action type] `[portable genuine damage upgrade upgrede]`）。
        private const int ForgeItemId = 10003912;
        private const int TargetItemId = 101010653;
        private const int MaterialItemId = 3326;
        private const int TicketItemId = 10008363;
        private const int OtherConsumableItemId = 10089614;
        private const short TargetSlot = 11;
        // 实机 0x01B7 把锻造炉自身的槽位填进材料槽字段（线上取证：materialSlot=94）。
        private const short ForgeSlot = 94;
        private const short MaterialSlot = 137;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== PORTABLE_FORGE_SEPARATE_UPGRADE selftest ===");
            TestPortableForgeDefinition();
            TestPortableUpgradeTransactions();
            TestPortableUpgradeRejections();
            TestNpcPathRegression();
            TestTicketPathRegression();
            TestCurrentPvf();
            Console.WriteLine(_failures == 0
                ? "PortableForgeSeparateUpgradeSelfTest OK"
                : $"PortableForgeSeparateUpgradeSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestPortableForgeDefinition()
        {
            var portable = StackableItemFile.Parse(@"
[action type]
`[portable genuine damage upgrade upgrede]` 
[/action type]
");
            Check(PortableForgeDefinition.IsPortableForge(portable),
                "portable forge action type recognised");

            var mixedCase = StackableItemFile.Parse(@"
[action type]
`[Portable Genuine Damage Upgrade Upgrede]` 
[/action type]
");
            Check(PortableForgeDefinition.IsPortableForge(mixedCase),
                "portable forge action type match is case-insensitive");

            var amplify = StackableItemFile.Parse(@"
[action type]
`[portable amplify]` 
[/action type]
");
            Check(!PortableForgeDefinition.IsPortableForge(amplify),
                "other portable action type rejected");

            Check(!PortableForgeDefinition.IsPortableForge(null)
                && !PortableForgeDefinition.IsPortableForge(new StackableItemFile()),
                "null and action-less stackable rejected");
        }

        private static void TestPortableUpgradeTransactions()
        {
            var table = CreateTable();
            var metadata = ResolveMetadata();
            var command = CreateCommand(ForgeSlot);

            var inventory = CreateInventory();
            Check(InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var success)
                && success.UpgradeSucceeded
                && success.OldLevel == 0 && success.NewLevel == 1
                && success.MaterialItemTemplateId == ForgeItemId
                && success.MaterialCost == 1
                && success.MaterialRemainingCount == 1
                && !success.NoticeRequired
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 1
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 1
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1000,
                "portable success raises forging by one, consumes one forge and no breath");

            inventory = CreateInventory(targetLevel: 1);
            Check(InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 9999, out var failure)
                && !failure.UpgradeSucceeded
                && failure.OldLevel == 1 && failure.NewLevel == 1
                && failure.MaterialItemTemplateId == ForgeItemId
                && failure.MaterialCost == 1
                && failure.MaterialRemainingCount == 1
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 1
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 1
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1000,
                "portable failure keeps level, still consumes one forge and no breath");

            inventory = CreateInventory(forgeCount: 1);
            Check(InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var lastForge)
                && lastForge.UpgradeSucceeded
                && lastForge.MaterialRemainingCount == 0
                && inventory.GetItem(InventoryListType.Main, ForgeSlot) == null
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1000,
                "portable use consumes the last forge from its own slot");

            inventory = CreateInventory(targetLevel: 4);
            Check(InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var notice)
                && notice.UpgradeSucceeded && notice.NewLevel == 5 && notice.NoticeRequired,
                "portable upgrade keeps the shared notice threshold");
        }

        private static void TestPortableUpgradeRejections()
        {
            var table = CreateTable();
            var metadata = ResolveMetadata();
            var command = CreateCommand(ForgeSlot);

            var inventory = CreateInventory(forgeCount: 0);
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var emptySlot)
                && emptySlot.ErrorCode == SeparateUpgradeResult.ErrorInvalidMaterial
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 0,
                "portable upgrade rejects an empty material slot");

            inventory = CreateInventory(forgeCount: 0);
            inventory.SetItem(InventoryListType.Main, ForgeSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = OtherConsumableItemId,
                Count = 3,
            });
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var nonForge)
                && nonForge.ErrorCode == SeparateUpgradeResult.ErrorInvalidMaterial
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 3
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 0,
                "portable upgrade rejects a non-forge consumable without consuming it");

            inventory = CreateInventory(targetLevel: 8);
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var maximum)
                && maximum.ErrorCode == SeparateUpgradeResult.ErrorMaxLevel
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 2,
                "portable upgrade rejects maximum forging level without consuming");

            inventory = CreateInventory();
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table,
                    MetadataWith(equipmentType: "[coat]"), () => 0, out var notWeapon)
                && notWeapon.ErrorCode == SeparateUpgradeResult.ErrorNotWeapon
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 2,
                "portable upgrade rejects non-weapon targets without consuming");

            inventory = CreateInventory(targetDurability: 39);
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table, metadata, () => 0, out var durability)
                && durability.ErrorCode == SeparateUpgradeResult.ErrorDurability
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 2,
                "portable upgrade rejects damaged weapons without consuming");

            inventory = CreateInventory();
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, command, table,
                    MetadataWith(rarity: 9), () => 0, out var unsupported)
                && unsupported.ErrorCode == SeparateUpgradeResult.ErrorUnsupported
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 2,
                "portable upgrade rejects unsupported rarity weights without consuming");

            inventory = CreateInventory();
            Check(!InventorySeparateUpgradeService.TryUpgradePortable(
                    inventory, CreateCommand(ForgeSlot, InventoryListType.PersonalCargo),
                    table, metadata, () => 0, out _)
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 2,
                "portable upgrade only accepts main or equipment targets");
        }

        private static void TestNpcPathRegression()
        {
            var table = CreateTable();
            var metadata = ResolveMetadata();

            var inventory = CreateInventory();
            Check(InventorySeparateUpgradeService.TryUpgrade(
                    inventory, CreateCommand(MaterialSlot), table, metadata, () => 0, out var success)
                && success.UpgradeSucceeded && success.NewLevel == 1
                && success.MaterialItemTemplateId == MaterialItemId
                && success.MaterialCost == 64
                && success.MaterialRemainingCount == 936
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 1
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 936
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 2,
                "NPC material path still consumes grade material only");

            inventory = CreateInventory(forgeCount: 0, materialCount: 0);
            Check(!InventorySeparateUpgradeService.TryUpgrade(
                    inventory, CreateCommand(MaterialSlot), table, metadata, () => 0, out var emptyMaterial)
                && emptyMaterial.ErrorCode == SeparateUpgradeResult.ErrorInvalidMaterial
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 0,
                "NPC path still rejects an empty material slot");
        }

        private static void TestTicketPathRegression()
        {
            var stackable = StackableItemFile.Parse(@"
[equipment separate reinforcement ticket]
7 100
`fixed` -1
[need material]
10008362 12
");
            Check(SeparateUpgradeTicketDefinition.TryParse(TicketItemId, stackable, out var ticket)
                && ticket.TargetLevel == 7 && ticket.SuccessWeight == 10000 && ticket.IsFixed,
                "fixed separate-upgrade ticket definition unchanged");

            var inventory = CreateInventory(targetLevel: 8, forgeCount: 0);
            inventory.SetItem(InventoryListType.Main, ForgeSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = TicketItemId,
                Count = 2,
            });
            Check(InventorySeparateUpgradeService.TryApplyTicket(
                    inventory, CreateCommand(ForgeSlot), ticket, CreateTable(), ResolveMetadata(),
                    () => 0, out var applied)
                && applied.UpgradeSucceeded && applied.NewLevel == 7
                && applied.MaterialCost == 1 && applied.MaterialRemainingCount == 1
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 7
                && inventory.GetItem(InventoryListType.Main, ForgeSlot).Count == 1,
                "fixed ticket path still overwrites forging and consumes one ticket");

            inventory = CreateInventory(forgeCount: 0);
            inventory.SetItem(InventoryListType.Main, ForgeSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = TicketItemId + 1,
                Count = 1,
            });
            Check(!InventorySeparateUpgradeService.TryApplyTicket(
                    inventory, CreateCommand(ForgeSlot), ticket, CreateTable(), ResolveMetadata(),
                    () => 0, out var substituted)
                && substituted.ErrorCode == SeparateUpgradeResult.ErrorInvalidMaterial
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 0,
                "ticket path still rejects a substituted slot item");
        }

        private static void TestCurrentPvf()
        {
            try
            {
                Check(PortableForgeDefinition.IsPortableForgeItem(ForgeItemId),
                    "current Script.pvf identifies the portable forge by item id");
                Check(!SeparateUpgradeTicketDefinition.TryLoad(ForgeItemId, out _),
                    "current Script.pvf portable forge is not a separate-upgrade ticket");
                Check(!PortableForgeDefinition.IsPortableForgeItem(MaterialItemId)
                    && !PortableForgeDefinition.IsPortableForgeItem(OtherConsumableItemId)
                    && !PortableForgeDefinition.IsPortableForgeItem(TicketItemId),
                    "current Script.pvf other stackables are not portable forges");
            }
            catch (Exception ex)
            {
                Check(false, $"current Script.pvf portable forge: {ex.Message}");
            }
        }

        private static SeparateUpgradeTable CreateTable()
            => SeparateUpgradeTable.Parse(@"
[table]
2 1 10000 1
3 2 5000 1.11
4 3 5000 1.21
6 4 3000 1.32
8 5 3000 1.42
13 9 1500 1.53
18 12 1500 1.63
25 18 750 1.74
[separate upgrade max]
8
[level]
8
[item weights by grade]
91 3326 46
[item weights by rarity]
0.4 0.7 1 1.25 1.4 1.1 1.3
");

        private static InventoryService CreateInventory(
            byte targetLevel = 0,
            ushort targetDurability = 40,
            int forgeCount = 2,
            int materialCount = 1000)
        {
            var inventory = new InventoryService(950001, 950002);
            inventory.SetItem(InventoryListType.Main, TargetSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = TargetItemId,
                Uid = 1,
                Durability = targetDurability,
                GenuineUpgrade = targetLevel,
            });
            if (forgeCount > 0)
            {
                inventory.SetItem(InventoryListType.Main, ForgeSlot, new ItemCore
                {
                    ItemKind = ItemCore.KindConsumable,
                    ItemId = ForgeItemId,
                    Count = forgeCount,
                });
            }
            if (materialCount > 0)
            {
                inventory.SetItem(InventoryListType.Main, MaterialSlot, new ItemCore
                {
                    ItemKind = ItemCore.KindMaterial,
                    ItemId = MaterialItemId,
                    Count = materialCount,
                });
            }
            return inventory;
        }

        private static SeparateUpgradeCommand CreateCommand(
            short materialSlot,
            InventoryListType targetListType = InventoryListType.Main)
            => new SeparateUpgradeCommand
            {
                TargetListType = targetListType,
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId,
                MaterialSlotIndex = materialSlot,
            };

        private static ItemMetadata ResolveMetadata()
            => MetadataWith();

        private static ItemMetadata MetadataWith(
            string equipmentType = "[weapon]",
            int rarity = 4)
            => new ItemMetadata
            {
                ItemKind = "equipment",
                EquipmentType = equipmentType,
                Grade = 91,
                MinimumLevel = 85,
                Rarity = rarity,
                Durability = 40,
            };

        private static void Check(bool condition, string label)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition) _failures++;
        }
    }
}
