using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class ChronicleGrowthSelfTest
    {
        private const int AccountId = 941109;
        private const int CharacterId = 941209;
        private const int TargetItemId = 135000;
        private const int NormalTicketId = 10094062;
        private const int AdvancedTicketId = 10094063;
        private const short TargetSlot = 13;
        private const short NormalTicketSlot = 105;
        private const short AdvancedTicketSlot = 106;
        private const short FragmentSlot = 125;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== CHRONICLE_GROWTH selftest ===");

            TestPvfParsing();
            TestProtocol();
            TestCostFormula();
            TestStore();

            Console.WriteLine(_failures == 0 ? "ChronicleGrowthSelfTest OK" : $"ChronicleGrowthSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestPvfParsing()
        {
            Check(ItemMetadataResolver.TryLoadStackableFile(NormalTicketId, out var normal)
                && normal.EmancipateTicket == 5
                && normal.EquipmentLevelEmancipate?.UpgradeLevel == 3
                && normal.EquipmentLevelEmancipate.Condition.Rarities.Contains(5)
                && normal.EquipmentLevelEmancipate.Condition.MinimumLevel == 70
                && normal.EquipmentLevelEmancipate.Condition.MaximumLevel == 86
                && normal.EquipmentLevelEmancipate.IgnoreIndexes.Contains(450114),
                "normal ticket PVF");
            Check(ItemMetadataResolver.TryLoadStackableFile(AdvancedTicketId, out var advanced)
                && advanced.EquipmentLevelEmancipate?.UpgradeLevel == 5,
                "advanced ticket PVF");
        }

        private static void TestProtocol()
        {
            var captured = Hex("69 00 EE 05 9A 00 0D 00 58 0F 02 00 01 7D 00 EF 0C 00 00");
            Check(ChronicleGrowthRequest.TryParse(captured, out var command)
                && command.TicketSlotIndex == NormalTicketSlot
                && command.TicketItemTemplateId == NormalTicketId
                && command.TargetSlotIndex == TargetSlot
                && command.TargetItemTemplateId == TargetItemId
                && command.Materials.Count == 1
                && command.Materials[0].SlotIndex == FragmentSlot
                && command.Materials[0].ItemTemplateId == ChronicleGrowthCostCalculator.FragmentItemTemplateId,
                "captured 0x010F request");
            Check(!ChronicleGrowthRequest.TryParse(captured[..^1], out _), "truncated request rejected");

            var optionVariant = (byte[])captured.Clone();
            optionVariant[12] = 0x04;
            Check(ChronicleGrowthRequest.TryParse(optionVariant, out var optionCommand)
                && optionCommand.Materials.Count == 1
                && optionCommand.Materials[0].SlotIndex == FragmentSlot,
                "option byte does not change request layout");

            var result = new ChronicleGrowthResult { GrowthSucceeded = true };
            result.Consumptions.Add(new ChronicleGrowthConsumption
                { ListType = InventoryListType.Main, SlotIndex = NormalTicketSlot, ConsumedCount = 1 });
            result.Consumptions.Add(new ChronicleGrowthConsumption
                { ListType = InventoryListType.Main, SlotIndex = FragmentSlot, ConsumedCount = 6 });
            var ack = ChronicleGrowthAckBuilder.BuildSuccess(result);
            Check(ack.Length == 17
                && ack[0] == 1 && ack[1] == 1 && ack[2] == 2
                && BitConverter.ToInt16(ack, 4) == NormalTicketSlot
                && BitConverter.ToInt32(ack, 6) == 1
                && BitConverter.ToInt16(ack, 11) == FragmentSlot
                && BitConverter.ToInt32(ack, 13) == 6,
                "success response consumptions");
        }

        private static void TestCostFormula()
        {
            Check(ChronicleGrowthCostCalculator.Calculate(70, Game.ItemUpgrade.EquipmentType.Coat, 0, 0, 0) == 6,
                "Lv70 +0 coat costs 6 fragments");
            Check(ChronicleGrowthCostCalculator.Calculate(70, Game.ItemUpgrade.EquipmentType.Coat, 3, 0, 0) == 7,
                "Lv70 +3 coat truncates to 7 fragments");
            var levels = new[] { 70, 73, 75, 76, 79, 80, 82, 85 };
            var forgingCosts = new[]
            {
                new[] { 7, 8, 9, 9, 10, 10, 11, 12 },
                new[] { 7, 9, 9, 10, 11, 11, 12, 13 },
                new[] { 8, 9, 10, 11, 12, 12, 13, 15 },
                new[] { 9, 11, 12, 12, 14, 14, 15, 17 },
                new[] { 10, 12, 13, 14, 15, 16, 17, 18 },
                new[] { 14, 16, 18, 18, 20, 21, 23, 25 },
                new[] { 18, 20, 22, 23, 26, 27, 28, 31 },
                new[] { 21, 24, 27, 28, 31, 32, 34, 37 },
                new[] { 7, 8, 9, 9, 10, 10, 11, 12 },
            };
            for (var forging = 0; forging < forgingCosts.Length; forging++)
            {
                for (var levelIndex = 0; levelIndex < levels.Length; levelIndex++)
                {
                    var expected = forgingCosts[forging][levelIndex];
                    Check(ChronicleGrowthCostCalculator.Calculate(levels[levelIndex],
                            Game.ItemUpgrade.EquipmentType.Weapon, 0, 0,
                            ChronicleGrowthCostCalculator.ResolveCostGenuineGrade(forging)) == expected,
                        $"Lv{levels[levelIndex]} forging +{forging} weapon costs {expected} fragments");
                }
            }
        }

        private static void TestStore()
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            inventory.SetItem(InventoryListType.Main, TargetSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = TargetItemId,
                Uid = 10001,
                Durability = 40,
                GenuineUpgrade = 8,
            });
            AddTicket(inventory, NormalTicketSlot, NormalTicketId);
            AddTicket(inventory, AdvancedTicketSlot, AdvancedTicketId);
            inventory.SetItem(InventoryListType.Main, FragmentSlot, new ItemCore
            {
                ItemKind = ItemCore.KindMaterial,
                ItemId = ChronicleGrowthCostCalculator.FragmentItemTemplateId,
                Count = 100,
            });

            var normal = CreateCommand(NormalTicketSlot, NormalTicketId);
            Check(ChronicleGrowthService.TryGrow(inventory, normal, out var normalResult)
                && normalResult.GrowthSucceeded
                && normalResult.OldLevel == 70
                && normalResult.NewLevel == 73
                && normalResult.RequiredFragmentCount == 6,
                "normal ticket grows equipment 70 to 73");
            Check(inventory.GetItem(InventoryListType.Main, TargetSlot).EmancipateEquipmentLevel == 3
                && inventory.GetItem(InventoryListType.Main, NormalTicketSlot) == null
                && inventory.GetItem(InventoryListType.Main, FragmentSlot).Count == 94,
                "normal growth mutates ItemCore and consumes atomically");

            var roundTrip = ItemCore.FromBytes(
                inventory.GetItem(InventoryListType.Main, TargetSlot).ToBytes());
            Check(roundTrip.EmancipateEquipmentLevel == 3
                && roundTrip.GenuineUpgrade == 8,
                "growth level survives ItemCore persistence codec");

            var advanced = CreateCommand(AdvancedTicketSlot, AdvancedTicketId);
            Check(ChronicleGrowthService.TryGrow(inventory, advanced, out var advancedResult)
                && advancedResult.GrowthSucceeded
                && advancedResult.OldLevel == 73
                && advancedResult.NewLevel == 78,
                "advanced ticket grows 73 to 78");

            var target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.EmancipateEquipmentLevel = 15;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
            AddTicket(inventory, AdvancedTicketSlot, AdvancedTicketId);
            Check(ChronicleGrowthService.TryGrow(inventory, advanced, out var cappedResult)
                && cappedResult.NewLevel == 86
                && inventory.GetItem(InventoryListType.Main, TargetSlot).EmancipateEquipmentLevel == 16,
                "advanced ticket caps at 86");

            AddTicket(inventory, AdvancedTicketSlot, AdvancedTicketId);
            Check(!ChronicleGrowthService.TryGrow(inventory, advanced, out var maximumResult)
                && maximumResult.ErrorCode == ChronicleGrowthResult.ErrorMaximumLevel
                && inventory.GetItem(InventoryListType.Main, AdvancedTicketSlot).Count == 1,
                "maximum level rejects without consuming");

            target = inventory.GetItem(InventoryListType.Main, TargetSlot).Copy();
            target.EmancipateEquipmentLevel = 0;
            inventory.SetItem(InventoryListType.Main, TargetSlot, target);
            inventory.SetItem(InventoryListType.Main, FragmentSlot, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = ChronicleGrowthCostCalculator.FragmentItemTemplateId,
                Uid = 100,
            });
            Check(!ChronicleGrowthService.TryGrow(inventory, advanced, out var invalidMaterialResult)
                && invalidMaterialResult.ErrorCode == ChronicleGrowthResult.ErrorInsufficientMaterial
                && inventory.GetItem(InventoryListType.Main, AdvancedTicketSlot).Count == 1,
                "equipment-shaped fragment row is rejected without consuming ticket");
        }

        private static ChronicleGrowthCommand CreateCommand(short ticketSlot, int ticketId)
        {
            var command = new ChronicleGrowthCommand
            {
                TicketSlotIndex = ticketSlot,
                TicketItemTemplateId = ticketId,
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId,
            };
            command.Materials.Add(new ChronicleGrowthMaterialRequest
            {
                SlotIndex = FragmentSlot,
                ItemTemplateId = ChronicleGrowthCostCalculator.FragmentItemTemplateId,
            });
            return command;
        }

        private static void AddTicket(
            InventoryService inventory,
            short slotIndex,
            int itemTemplateId)
        {
            inventory.SetItem(InventoryListType.Main, slotIndex, new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = itemTemplateId,
                Count = 1,
            });
        }
        private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

        private static void Check(bool condition, string label)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition) _failures++;
        }
    }
}
