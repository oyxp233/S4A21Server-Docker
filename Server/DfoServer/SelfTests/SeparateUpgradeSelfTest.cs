using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class SeparateUpgradeSelfTest
    {
        private const int TargetItemId = 101010653;
        private const int MaterialItemId = 3326;
        private const int TicketItemId = 10008363;
        private const short TargetSlot = 11;
        private const short MaterialSlot = 134;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== SEPARATE_UPGRADE selftest ===");
            TestProtocol();
            var table = TestPvfParsing();
            TestTransactions(table);
            TestTicketTransactions();
            TestCurrentPvf();
            TestCurrentPvfTickets();
            Console.WriteLine(_failures == 0
                ? "SeparateUpgradeSelfTest OK"
                : $"SeparateUpgradeSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestProtocol()
        {
            var captured = Hex("03-0B-00-DD-4C-05-06-86-00-16-00-00-00-2B-31-33-20-E8-8D-92-E5-8F-A4-E9-81-97-E5-B0-98-E5-A4-AA-E5-88-80-00");
            Check(SeparateUpgradeRequest.TryParse(captured, out var request)
                && request.TargetListType == InventoryListType.Equipment
                && request.TargetSlotIndex == TargetSlot
                && request.TargetItemTemplateId == TargetItemId
                && request.MaterialSlotIndex == MaterialSlot, "captured 0x01B7 request");
            Check(!SeparateUpgradeRequest.TryParse(captured[..^1], out _), "truncated request rejected");
            var badLength = (byte[])captured.Clone();
            badLength[9]++;
            Check(!SeparateUpgradeRequest.TryParse(badLength, out _), "invalid name length rejected");
            var badList = (byte[])captured.Clone();
            badList[0] = 2;
            Check(!SeparateUpgradeRequest.TryParse(badList, out _), "unknown client list rejected");

            var command = request.ToCommand();
            var ack = SeparateUpgradeAckBuilder.BuildSuccess(new SeparateUpgradeResult
            {
                Command = command,
                UpgradeSucceeded = true,
                OldLevel = 0,
                NewLevel = 1,
                MaterialRemainingCount = 940,
            });
            Check(ack.Length == 13
                && ack[0] == 1
                && BitConverter.ToInt16(ack, 1) == MaterialSlot
                && BitConverter.ToInt32(ack, 3) == 940
                && ack[7] == 0 && ack[8] == 0 && ack[9] == 1
                && ack[10] == 3
                && BitConverter.ToInt16(ack, 11) == TargetSlot, "success ACK layout");
            Check(Convert.ToHexString(SeparateUpgradeAckBuilder.BuildError(22)) == "0016", "error ACK layout");

            var notice = SeparateUpgradeNoticeBuilder.Build(new SeparateUpgradeResult
            {
                Command = command,
                UpgradeSucceeded = true,
                NewLevel = 6,
                TargetReinforceLevel = 12,
                TargetItemSnapshot = CreateNoticeTarget(),
            }, 0x1234);
            Check(Convert.ToHexString(notice) == "0E013412DD4C05060C060107080900FF",
                "current-client 0x0056 subtype 0x0E notice layout");

            var upgradeNotice = ItemUpgradeNoticeBuilder.Build(new ItemUpgradeResult
            {
                UpgradeSucceeded = true,
                NewLevel = 13,
                TargetItemTemplateId = TargetItemId,
                TargetItemSnapshot = CreateNoticeTarget(),
            }, 0x1234);
            Check(Convert.ToHexString(upgradeNotice) == "01013412DD4C05060D0107080900FF",
                "shared random-option writer keeps upgrade notice item state");
        }

        private static SeparateUpgradeTable TestPvfParsing()
        {
            var content = @"
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
";
            var table = SeparateUpgradeTable.Parse(content);
            Check(table.IsStructurallyValid && table.MaxLevel == 8 && table.Levels.Count == 8,
                "separate table levels");
            Check(table.MaterialsByGrade.TryGetValue(91, out var targetMaterial)
                && targetMaterial.ItemTemplateId == MaterialItemId && targetMaterial.BaseCount == 46,
                "target PVF grade material triple");
            Check(table.ItemWeightsByRarity.Count == 7
                && Math.Abs(table.ItemWeightsByRarity[4] - 1.4) < 0.000001, "rarity weights");
            return table;
        }

        private static void TestTransactions(SeparateUpgradeTable table)
        {
            var inventory = CreateInventory();
            var command = CreateCommand();
            var equippedTarget = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            inventory.RemoveItem(InventoryListType.Main, TargetSlot);
            inventory.SetItem(InventoryListType.Equipment, TargetSlot, equippedTarget);
            var equippedCommand = CreateCommand();
            equippedCommand.TargetListType = InventoryListType.Equipment;
            Check(InventorySeparateUpgradeService.TryUpgrade(
                    inventory, equippedCommand, table, ResolveMetadata(), () => 0, out var equippedSuccess)
                && equippedSuccess.UpgradeSucceeded
                && inventory.GetItem(InventoryListType.Equipment, TargetSlot).GenuineUpgrade == 1
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 936,
                "equipped weapon upgrades while material is consumed from main inventory");

            inventory = CreateInventory();
            var failureTarget = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            failureTarget.GenuineUpgrade = 5;
            inventory.SetItem(InventoryListType.Main, TargetSlot, failureTarget);
            Check(InventorySeparateUpgradeService.TryUpgrade(
                    inventory, command, table, ResolveMetadata(), () => 9999, out var failure)
                && !failure.UpgradeSucceeded && failure.OldLevel == 5 && failure.NewLevel == 5
                && failure.NoticeRequired
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 902
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 5,
                "level-five-or-higher failure consumes material and broadcasts without lowering");

            inventory = CreateInventory();
            var target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.Durability--;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
            Check(!InventorySeparateUpgradeService.TryUpgrade(
                    inventory, command, table, ResolveMetadata(), () => 0, out var durability)
                && durability.ErrorCode == SeparateUpgradeResult.ErrorDurability
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1000,
                "durability rejection does not consume");

            inventory = CreateInventory();
            target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.GenuineUpgrade = 8;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
            Check(!InventorySeparateUpgradeService.TryUpgrade(
                    inventory, command, table, ResolveMetadata(), () => 0, out var maximum)
                && maximum.ErrorCode == SeparateUpgradeResult.ErrorMaxLevel
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1000,
                "maximum rejection does not consume");

            inventory = CreateInventory(63);
            Check(!InventorySeparateUpgradeService.TryUpgrade(
                    inventory, command, table, ResolveMetadata(), () => 0, out var insufficient)
                && insufficient.ErrorCode == SeparateUpgradeResult.ErrorInvalidMaterial
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 63,
                "insufficient material does not consume");

            inventory = CreateInventory();
            Check(!InventorySeparateUpgradeService.TryUpgrade(
                    inventory, command, table, new ItemMetadata
                    {
                        ItemKind = "equipment",
                        EquipmentType = "[coat]",
                        MinimumLevel = 85,
                        Rarity = 4,
                        Durability = 40,
                    }, () => 0, out var notWeapon)
                && notWeapon.ErrorCode == SeparateUpgradeResult.ErrorNotWeapon
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1000,
                "non-weapon rejection does not consume");

            inventory = CreateInventory(10000);
            var expectedCosts = new[] { 64, 71, 77, 85, 91, 98, 104, 112 };
            var remaining = 10000;
            var sequenceMatches = true;
            foreach (var expectedCost in expectedCosts)
            {
                if (!InventorySeparateUpgradeService.TryUpgrade(
                        inventory, command, table, ResolveMetadata(), () => 0, out var step)
                    || step.MaterialCost != expectedCost
                    || step.NoticeRequired != (step.NewLevel >= 5))
                {
                    sequenceMatches = false;
                    break;
                }
                remaining -= expectedCost;
            }
            Check(sequenceMatches
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 8
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == remaining,
                "PVF grade 91 material sequence matches client display");
        }

        private static void TestTicketTransactions()
        {
            var stackable = StackableItemFile.Parse(@"
[stackable type]
`[etc]` 1
[equipment separate reinforcement ticket]
7 100
`fixed` -1
[need material]
10008362 12
");
            Check(SeparateUpgradeTicketDefinition.TryParse(TicketItemId, stackable, out var ticket)
                && ticket.ItemTemplateId == TicketItemId
                && ticket.TargetLevel == 7
                && ticket.SuccessWeight == 10000
                && ticket.IsFixed,
                "fixed separate-upgrade ticket PVF definition");

            var inventory = CreateInventory();
            inventory.SetItem(InventoryListType.Main, MaterialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = TicketItemId,
                Count = 2,
            });
            var target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.GenuineUpgrade = 8;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
            Check(InventorySeparateUpgradeService.TryApplyTicket(
                    inventory, CreateCommand(), ticket, CreateTicketTable(), ResolveMetadata(), () => 0, out var success)
                && success.UpgradeSucceeded
                && success.OldLevel == 8 && success.NewLevel == 7
                && success.MaterialCost == 1 && success.MaterialRemainingCount == 1
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 7
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1,
                "fixed ticket overwrites higher forging and consumes only one ticket");

            inventory = CreateInventory();
            inventory.SetItem(InventoryListType.Main, MaterialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = TicketItemId + 1,
                Count = 1,
            });
            Check(!InventorySeparateUpgradeService.TryApplyTicket(
                    inventory, CreateCommand(), ticket, CreateTicketTable(), ResolveMetadata(), () => 0, out var forgedSlot)
                && forgedSlot.ErrorCode == SeparateUpgradeResult.ErrorInvalidMaterial
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 0,
                "ticket service rejects a substituted slot item");

            var additionalStackable = StackableItemFile.Parse(@"
[equipment separate reinforcement ticket]
1 100
`additional` -1
");
            Check(SeparateUpgradeTicketDefinition.TryParse(TicketItemId, additionalStackable, out var additional)
                && additional.IsAdditional && !additional.IsFixed,
                "additional separate-upgrade ticket PVF definition");
            inventory = CreateInventory();
            inventory.SetItem(InventoryListType.Main, MaterialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = TicketItemId,
                Count = 2,
            });
            target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.GenuineUpgrade = 7;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
            Check(InventorySeparateUpgradeService.TryApplyTicket(
                    inventory, CreateCommand(), additional, CreateTicketTable(), ResolveMetadata(), () => 0, out var added)
                && added.NewLevel == 8
                && inventory.GetItem(InventoryListType.Main, TargetSlot).GenuineUpgrade == 8,
                "additional ticket increases forging up to global maximum");
            Check(!InventorySeparateUpgradeService.TryApplyTicket(
                    inventory, CreateCommand(), additional, CreateTicketTable(), ResolveMetadata(), () => 0, out var capped)
                && capped.ErrorCode == SeparateUpgradeResult.ErrorMaxLevel
                && inventory.GetItem(InventoryListType.Main, MaterialSlot).Count == 1,
                "additional ticket rejects maximum-level weapon without consuming");
        }

        private static SeparateUpgradeTable CreateTicketTable()
            => SeparateUpgradeTable.Parse(@"
[table]
1 1 10000 1
[separate upgrade max]
8
[level]
8
[item weights by grade]
91 3326 1
[item weights by rarity]
1 1 1 1 1 1 1
");

        private static void TestCurrentPvf()
        {
            try
            {
                var table = SeparateUpgradeTableProvider.Get();
                Check(table.MaxLevel == 8
                    && table.Levels.Count >= table.MaxLevel
                    && table.MaterialsByGrade.TryGetValue(91, out var material)
                    && material.ItemTemplateId == MaterialItemId && material.BaseCount == 46,
                    "current Script.pvf separate-upgrade table");
                var metadata = ItemMetadataResolver.Resolve(TargetItemId);
                Check(metadata.MinimumLevel == 85 && metadata.Grade == 91
                    && metadata.Rarity == 4
                    && EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType)),
                    "current target exposes PVF grade 91 and minimum level 85 separately");
            }
            catch (Exception ex)
            {
                Check(false, $"current Script.pvf: {ex.Message}");
            }
        }

        private static void TestCurrentPvfTickets()
        {
            try
            {
                var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
                var count = 0;
                var allSupported = true;
                foreach (var entry in list.Entries)
                {
                    var text = PvfArchiveAccessor.ReadText(Path.Combine("stackable", entry.FilePath));
                    if (text.IndexOf("[equipment separate reinforcement ticket]", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    count++;
                    var stackable = StackableItemFile.Parse(text);
                    var source = stackable.EquipmentSeparateReinforcementTicket;
                    var supported = SeparateUpgradeTicketDefinition.TryParse(entry.Id, stackable, out var definition);
                    allSupported &= supported;
                    if (!supported)
                    {
                        Console.WriteLine(
                            $"  [INFO] unsupported separate ticket item={entry.Id} path={entry.FilePath} " +
                            $"level={source?.TargetLevel} rate={source?.SuccessRatePercent} " +
                            $"mode={source?.ApplyMode} apply={source?.ApplyValue}");
                    }
                    if (supported)
                    {
                        allSupported &= definition.TargetLevel == source.TargetLevel
                            && definition.SuccessWeight == source.SuccessRatePercent * 100;
                    }
                }

                Check(count > 0 && allSupported,
                    $"current PVF separate-upgrade tickets supported count={count}");
            }
            catch (Exception ex)
            {
                Check(false, $"current PVF separate-upgrade tickets: {ex.Message}");
            }
        }

        private static InventoryService CreateInventory(int materialCount = 1000)
        {
            var inventory = new InventoryService(950001, 950002);
            inventory.SetItem(InventoryListType.Main, TargetSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = TargetItemId,
                Uid = 1,
                Durability = 40,
            });
            inventory.SetItem(InventoryListType.Main, MaterialSlot, new ItemCore
            {
                ItemKind = ItemCore.KindMaterial,
                ItemId = MaterialItemId,
                Count = materialCount,
            });
            return inventory;
        }

        private static SeparateUpgradeCommand CreateCommand() => new SeparateUpgradeCommand
        {
            TargetListType = InventoryListType.Main,
            TargetSlotIndex = TargetSlot,
            TargetItemTemplateId = TargetItemId,
            MaterialSlotIndex = MaterialSlot,
        };

        private static ItemMetadata ResolveMetadata() => new ItemMetadata
        {
            ItemKind = "equipment",
            EquipmentType = "[weapon]",
            Grade = 91,
            MinimumLevel = 85,
            Rarity = 4,
            Durability = 40,
        };

        private static ItemCore CreateNoticeTarget()
        {
            var target = new ItemCore();
            target.RandomOption0.Type = 7;
            target.RandomOption0.Value1 = 8;
            target.RandomOption0.Value2 = 9;
            target.RandomOptionChangedIndex = ItemCore.RandomOptionChangedIndexDefault;
            return target;
        }

        private static byte[] Hex(string value) => Convert.FromHexString(value.Replace("-", string.Empty));

        private static void Check(bool condition, string label)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition) _failures++;
        }
    }
}
