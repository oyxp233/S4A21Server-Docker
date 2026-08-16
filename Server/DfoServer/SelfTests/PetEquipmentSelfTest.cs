using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class PetEquipmentSelfTest
    {
        private const short PetInventorySourceSlot = 48;
        private const short EquippedPetSlot = 24;
        private const int MiniBloodPetItemId = 0x17E69F80;
        private const int PetSerial = 37;
        private const int ExplicitCreatureExtra = 1234;
        private const int PetEnchantCardItemId = 920024;
        private const byte PetEnchantUpgradeCount = 3;
        private const byte PetTradeRestriction = 1;
        private const byte PetRemainUseCount = 2;
        private const int Marker16WireOffset = 22;

        public static int Run()
        {
            Console.WriteLine("=== PET_EQUIPMENT selftest ===");

            var failures = 0;
            Check("sample pet is pet inventory equipment",
                ItemMetadataResolver.IsPetInventoryEquipment(MiniBloodPetItemId),
                ref failures);
            Check("compound item success ACK carries deleted and reward entries",
                BytesEqual(
                    CompoundItemAckBuilder.Build(new CompoundItemRecipeResult
                    {
                        SourceSlotIndex = 106,
                        RequestedCount = 1,
                        DeletedEntries =
                        {
                            new CompoundItemDeletedEntry
                            {
                                ListType = InventoryListType.Main,
                                SlotIndex = 106,
                                Count = 1,
                                ItemTemplateId = 0x0029F420,
                            },
                        },
                        Rewards =
                        {
                            new BoosterRewardResult
                            {
                                ListType = InventoryListType.Main,
                                SlotIndex = 106,
                                ItemTemplateId = 0x0029F42C,
                                StackCount = 1,
                                GrantedCount = 1,
                            },
                        },
                    }),
                    new byte[]
                    {
                        0x01,
                        0x01,
                        0x00, 0x6A, 0x00, 0x01, 0x00, 0x00, 0x00,
                        0x01,
                        0x00, 0x6A, 0x00, 0x2C, 0xF4, 0x29, 0x00, 0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                    }),
                ref failures);
            Check("compound item error ACK is compact failure body",
                BytesEqual(
                    CompoundItemAckBuilder.BuildError(21),
                    new byte[] { 0x00, 0x15 }),
                ref failures);

            var raw = MakeEquipListCodec.BuildEntryFromDisplayFields(
                EquippedPetSlot,
                MiniBloodPetItemId,
                new MakeEquipListCodec.DisplayFields { InstanceValue = PetSerial });
            Check("pet body equipment protocol entry keeps serial separate from creature extra",
                raw.Length >= 28
                && BitConverter.ToInt32(raw, 5) == PetSerial
                && BitConverter.ToInt32(raw, 24) == 0,
                ref failures);

            var rawWithExtra = MakeEquipListCodec.BuildEntryFromDisplayFields(
                EquippedPetSlot,
                MiniBloodPetItemId,
                new MakeEquipListCodec.DisplayFields
                {
                    InstanceValue = PetSerial,
                    CreatureExtra = ExplicitCreatureExtra,
                });
            var fieldsWithExtra = MakeEquipListCodec.ParseDisplayFields(rawWithExtra);
            Check("pet body equipment protocol entry preserves explicit creature extra",
                fieldsWithExtra.InstanceValue == PetSerial
                && fieldsWithExtra.CreatureExtra == ExplicitCreatureExtra,
                ref failures);

            var pet = ItemCore.Create(ItemCore.KindCreature, MiniBloodPetItemId);
            pet.Value = PetSerial;
            pet.EnchantCardId = PetEnchantCardItemId;
            pet.EnchantUpgradeCount = PetEnchantUpgradeCount;
            pet.TradeRestriction = PetTradeRestriction;
            pet.RemainUseCount = PetRemainUseCount;
            var petRoundtrip = ItemCore.FromBytes(pet.ToBytes());
            Check("pet ItemCore keeps creature uid and enchant fields",
                petRoundtrip.ItemKind == ItemCore.KindCreature
                && petRoundtrip.ItemId == MiniBloodPetItemId
                && petRoundtrip.Value == PetSerial
                && petRoundtrip.EnchantCardId == PetEnchantCardItemId
                && petRoundtrip.EnchantUpgradeCount == PetEnchantUpgradeCount,
                ref failures);
            Check("pet ItemCore keeps seal trade restriction fields",
                petRoundtrip.TradeRestriction == PetTradeRestriction
                && petRoundtrip.RemainUseCount == PetRemainUseCount,
                ref failures);

            var inventory = new InventoryService(163002, 163002);
            Check("online pet inventory accepts pet body slot",
                inventory.SetItem(InventoryListType.Pet, PetInventorySourceSlot, pet),
                ref failures);
            inventory.CreatureDetails.Put(new CreatureDetail
            {
                Uid = PetSerial,
                Field04 = 100,
                ModeFlag = 0,
                ProgressValue32 = 10,
                FieldAfterValue32 = 1,
            });
            Check("online pet detail builds creature list entry",
                PetInventoryAccessor.TryBuildCreatureItemEntry(inventory, PetSerial, out var entry)
                && entry.CreatureKey == PetSerial
                && entry.Field04 == 100
                && entry.ProgressValue32 == 10,
                ref failures);

            Check(
                "equipment keeps the ITEM_LIST -1 marker sentinel",
                WriteCommonMarker(
                    ItemCore.KindEquipment,
                    ItemCore.Marker16Default) == ItemCore.Marker16Default,
                ref failures);
            Check(
                "stackable ITEM_LIST maps the internal -1 marker to wire zero",
                WriteCommonMarker(
                    ItemCore.KindMaterial,
                    ItemCore.Marker16Default) == 0,
                ref failures);
            Check(
                "explicit common ITEM_LIST markers are preserved",
                WriteCommonMarker(ItemCore.KindMaterial, 731) == 731,
                ref failures);
            Check(
                "avatar ITEM_LIST maps the internal -1 marker to wire zero",
                WriteAvatarMarker(ItemCore.Marker16Default) == 0,
                ref failures);
            Check(
                "non-creature pet ITEM_LIST maps the internal -1 marker to wire zero",
                WritePetMarker(ItemCore.Marker16Default) == 0,
                ref failures);
            Check(
                "creature ITEM_LIST keeps its resolved remaining-time marker",
                WriteCreatureMarker(541) == 541,
                ref failures);

            try
            {
                using var connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                CreateCreatureRows(connection);

                var orderedInventory = new InventoryService(1002, 1002);
                orderedInventory.CreatureDetails.LoadForCharacter(
                    connection,
                    1002);
                var snapshot = PetInventoryAccessor.BuildCreatureItemListSnapshot(
                    orderedInventory);
                var actualOrder = snapshot.Entries
                    .Select(candidate => candidate.CreatureKey)
                    .ToArray();

                Check(
                    "0x0069 creature details follow persisted sort_order",
                    actualOrder.SequenceEqual(new[] { 20, 10, 30 }),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "FAIL creature ordering threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static int WriteCommonMarker(byte itemKind, int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(itemKind, marker16);
            ItemListProtocolWriter.WriteCommonEntry84(writer, 3, core);
            return ReadMarker(writer);
        }

        private static int WriteAvatarMarker(int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(ItemCore.KindAvatar, marker16);
            ItemListProtocolWriter.WriteAvatarEntry126(writer, 0, core, null);
            return ReadMarker(writer);
        }

        private static int WritePetMarker(int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(ItemCore.KindCreatureEquipment, marker16);
            ItemListProtocolWriter.WritePetEntry84(writer, 0, core);
            return ReadMarker(writer);
        }

        private static int WriteCreatureMarker(int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(ItemCore.KindCreature, marker16);
            ItemListProtocolWriter.WritePetCreatureEntry84(
                writer,
                0,
                core,
                null);
            return ReadMarker(writer);
        }

        private static ItemCore CreateCore(byte itemKind, int marker16)
        {
            var core = ItemCore.Create(itemKind, 0);
            core.Marker16 = marker16;
            return core;
        }

        private static int ReadMarker(GamePacketWriter writer)
        {
            var body = writer.ToArray();
            return BitConverter.ToInt32(body, Marker16WireOffset);
        }

        private static void CreateCreatureRows(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE character_creatures (
    character_id INTEGER NOT NULL,
    sort_order INTEGER NOT NULL,
    creature_key INTEGER NOT NULL,
    field04 INTEGER NOT NULL,
    mode_flag INTEGER NOT NULL,
    progress_value INTEGER NOT NULL,
    mode1_field0a INTEGER NOT NULL,
    mode1_field0b INTEGER NOT NULL,
    field_after_value INTEGER NOT NULL,
    creature_text BLOB NOT NULL,
    tail_flag INTEGER NOT NULL,
    extra_json TEXT NOT NULL
);
CREATE INDEX idx_character_creatures_key
    ON character_creatures(character_id, creature_key);
INSERT INTO character_creatures VALUES
    (1002, 2, 30, 100, 0, 30, 0, 0, 3, X'33', 0, '{}'),
    (1002, 1, 10, 100, 0, 10, 0, 0, 2, X'31', 0, '{}'),
    (1002, 0, 20, 100, 0, 20, 0, 0, 1, X'32', 0, '{}');";
            command.ExecuteNonQuery();
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
