using System;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;

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

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
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
