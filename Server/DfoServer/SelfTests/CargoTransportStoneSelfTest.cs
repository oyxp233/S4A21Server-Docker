using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class CargoTransportStoneSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CARGO_TRANSPORT_STONE selftest ===");
            var failures = 0;

            VerifyRequestAndAck(ref failures);
            VerifyStackableParser(ref failures);
            VerifyConfigParser(ref failures);
            VerifyLifecycleOptions(ref failures);
            VerifyRealPvfSamples(ref failures);

            Console.WriteLine(failures == 0
                ? "CARGO_TRANSPORT_STONE selftest passed"
                : $"CARGO_TRANSPORT_STONE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRequestAndAck(ref int failures)
        {
            var body = new byte[]
            {
                0x05, 0x00,
                0x09, 0x00,
                0x00,
                0x00, 0x00, 0x00, 0x00,
            };

            Check(
                "0x022E request parses captured equipment payload",
                CargoTransportStoneRequestParser.TryParse(body, out var request)
                && request.StoneSlotIndex == 5
                && request.TargetSlotIndex == 9
                && !request.IsCreatureTransportStone
                && request.TargetCharacterSlotIndex == 0,
                ref failures);

            var result = new CargoTransportStoneResult
            {
                Request = request,
                Status = CargoTransportStoneStatus.Success,
                AckRemainingStoneCount = 9,
                AckParameter = 8,
                AckMode = 0,
            };
            var ack = CargoTransportItemAckBuilder.Build(result);
            Check(
                "0x022E equipment ACK writes remaining stone count and cargo slot",
                ack.SequenceEqual(new byte[]
                {
                    0x01,
                    0x05, 0x00,
                    0x09, 0x00,
                    0x09, 0x00,
                    0x08, 0x00,
                    0x00,
                }),
                ref failures);

            var packet = GamePacketEnvelopeBuilder.Build(0x01, 0x022E, ack);
            Check(
                "0x022E ACK envelope size matches captured packet",
                packet.Length == 25
                && BitConverter.ToUInt16(packet, 1) == 0x022E
                && BitConverter.ToInt32(packet, 3) == 25,
                ref failures);

            var creatureBody = new byte[]
            {
                0x03, 0x00,
                0x00, 0x00,
                0x01,
                0x03, 0x00, 0x00, 0x00,
            };

            Check(
                "0x022E request parses captured creature payload",
                CargoTransportStoneRequestParser.TryParse(creatureBody, out var creatureRequest)
                && creatureRequest.StoneSlotIndex == 3
                && creatureRequest.TargetSlotIndex == 0
                && creatureRequest.IsCreatureTransportStone
                && creatureRequest.TargetCharacterSlotIndex == 3,
                ref failures);

            var creatureAck = CargoTransportItemAckBuilder.Build(new CargoTransportStoneResult
            {
                Request = creatureRequest,
                Status = CargoTransportStoneStatus.Success,
                AckRemainingStoneCount = 8,
                AckParameter = 3,
                AckMode = 1,
            });
            Check(
                "0x022E creature ACK selects client mail-notice branch",
                creatureAck.SequenceEqual(new byte[]
                {
                    0x01,
                    0x03, 0x00,
                    0x00, 0x00,
                    0x08, 0x00,
                    0x03, 0x00,
                    0x01,
                }),
                ref failures);

            Check(
                "0x022E failure ACK writes client error code",
                CargoTransportItemAckBuilder.Build(new CargoTransportStoneResult
                {
                    Request = request,
                    Status = CargoTransportStoneStatus.AccountCargoFull,
                }).SequenceEqual(new byte[] { 0x00, 0x04 }),
                ref failures);
        }

        private static void VerifyStackableParser(ref int failures)
        {
            var sameLine = PvfLib.StackableItemFile.Parse(@"
[action type]
`[cargo transport stone]` 7
[/action type]
[Rarity possible explain]
0 0 0 1 1 0 0
[/Rarity possible explain]
");
            Check(
                "stackable parser reads same-line cargo action and rarity explain",
                sameLine.ActionTypeName == "[cargo transport stone]"
                && sameLine.ActionTypeParams.Count == 1
                && sameLine.ActionTypeParams[0] == 7
                && sameLine.RarityPossibleExplain.Count == 7
                && sameLine.RarityPossibleExplain[3] == 1
                && sameLine.RarityPossibleExplain[4] == 1,
                ref failures);

            var splitLine = PvfLib.StackableItemFile.Parse(@"
[action type]
`[creature transport stone]`
16
[/action type]
");
            Check(
                "stackable parser reads split-line creature action parameter",
                splitLine.ActionTypeName == "[creature transport stone]"
                && splitLine.ActionTypeParams.Count == 1
                && splitLine.ActionTypeParams[0] == 16,
                ref failures);
        }

        private static void VerifyConfigParser(ref int failures)
        {
            var config = CargoTransportStoneConfigProvider.Parse(@"
[stone type]
7
[cargo transport stone grade]
1 1 11 1 21 1
[/cargo transport stone grade]
[cargo transport stone enable equip type]
`[weapon]` `[coat]`
[/cargo transport stone enable equip type]
[cargo transport stone except index]
100
[/cargo transport stone except index]
[cargo transport stone include index]
200
[/cargo transport stone include index]
[ui index]
220
[/ui index]
[/stone type]
");
            Check(
                "cargo transport stone config parses stone type 7",
                config.TryGetValue(7, out var definition)
                && definition.UiIndex == 220
                && definition.EnabledEquipmentTypes.Contains(EquipmentType.Weapon)
                && definition.EnabledEquipmentTypes.Contains(EquipmentType.Coat)
                && definition.AllowsLevel(1)
                && definition.AllowsLevel(30)
                && !definition.AllowsLevel(31)
                && !definition.AllowsItemId(100)
                && definition.AllowsItemId(200)
                && !definition.AllowsItemId(201),
                ref failures);
        }

        private static void VerifyLifecycleOptions(ref int failures)
        {
            const long now = 1700000500;
            const int itemId = 88001;
            var inventory = new InventoryService(88002, 88003);
            var stone = ItemCore.Create(ItemCore.KindConsumable, itemId);
            stone.Count = 1;
            inventory.SetItem(InventoryListType.Main, 10, stone);
            inventory.ItemStates.Upsert(ItemStateKinds.Effect, itemId, (int)now + 60);

            var stackable = new PvfLib.StackableItemFile
            {
                HasEffectMaintenance = true,
                StatChangeDurationMilliseconds = 1800000,
                HasCooltimeMaintenance = true,
                CoolTime = 10000,
            };
            var plan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                10,
                itemId,
                now,
                1,
                stackable,
                checkEffectMaintenance: false,
                checkCooltimeMaintenance: true);
            Check(
                "cargo lifecycle ignores active effect state",
                plan.Success
                && plan.EffectExpireTime == 0
                && plan.CooltimeExpireTime == now + 10,
                ref failures);

            inventory.ItemStates.Upsert(ItemStateKinds.Cooltime, itemId, (int)now + 60);
            var cooltimePlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                10,
                itemId,
                now,
                1,
                stackable,
                checkEffectMaintenance: false,
                checkCooltimeMaintenance: true);
            Check(
                "cargo lifecycle rejects active cooltime state",
                cooltimePlan.Status == InventoryItemLifecycleStatus.CooltimeActive,
                ref failures);
        }

        private static void VerifyRealPvfSamples(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF sample checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            var cargo = PvfLib.StackableItemFile.Parse(PvfArchiveAccessor.ReadText(
                "stackable/cash/chn_20140325_strong_stone/strong_stone_2683479.stk"));
            Check(
                "real PVF cargo stone sample uses stone type 7",
                cargo.ActionTypeName == "[cargo transport stone]"
                && cargo.ActionTypeParams.Count > 0
                && cargo.ActionTypeParams[0] == 7
                && cargo.RarityPossibleExplain.SequenceEqual(new[] { 0, 0, 0, 1, 1, 0, 0 }),
                ref failures);

            var creature = PvfLib.StackableItemFile.Parse(PvfArchiveAccessor.ReadText(
                "stackable/cash/chn_490700208.stk"));
            Check(
                "real PVF creature stone sample uses stone type 16",
                creature.ActionTypeName == "[creature transport stone]"
                && creature.ActionTypeParams.Count > 0
                && creature.ActionTypeParams[0] == 16
                && creature.RarityPossibleExplain.SequenceEqual(new[] { 1, 1, 1, 1, 0, 0, 0 }),
                ref failures);

            Check(
                "real PVF cargotransportstone.etc has type 7 and 16",
                CargoTransportStoneConfigProvider.TryGetDefinition(7, out var type7)
                && type7.EnabledEquipmentTypes.Contains(EquipmentType.Weapon)
                && CargoTransportStoneConfigProvider.TryGetDefinition(16, out var type16)
                && type16.EnabledEquipmentTypes.Contains(EquipmentType.Creature),
                ref failures);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine("[PASS] " + name);
                return;
            }

            failures++;
            Console.WriteLine("[FAIL] " + name);
        }
    }
}
