using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;

namespace DfoServer.SelfTests
{
    public static class CompoundItemAckSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== COMPOUND_ITEM_ACK selftest ===");
            var failures = 0;

            Check(
                "normal recipe ACK keeps remaining-count fields and zero reserved reward bytes",
                VerifyNormalRecipeAck(),
                ref failures);

            Check(
                "equipment transform reward keeps enhance fields out of the ACK",
                VerifyEquipmentRewardAck(),
                ref failures);

            Check(
                "reward entries keep a fixed stride with a zero reserved packet tail",
                VerifyMultiRewardEntryStride(),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "COMPOUND_ITEM_ACK selftest passed."
                    : $"COMPOUND_ITEM_ACK selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool VerifyNormalRecipeAck()
        {
            var result = new CompoundItemRecipeResult();
            result.DeletedEntries.Add(new CompoundItemDeletedEntry
            {
                ListType = InventoryListType.Main,
                SlotIndex = 4,
                Count = 1,
                RemainingCount = 10,
                ItemTemplateId = 100,
                SourceSnapshot = CreateStackable(100, 11),
            });
            result.DeletedEntries.Add(new CompoundItemDeletedEntry
            {
                ListType = InventoryListType.Main,
                SlotIndex = 359,
                Count = 1,
                RemainingCount = 100,
                ItemTemplateId = 101,
                SourceSnapshot = CreateStackable(101, 101),
            });
            result.DeletedEntries.Add(new CompoundItemDeletedEntry
            {
                ListType = InventoryListType.Main,
                SlotIndex = 3,
                Count = 1,
                RemainingCount = 1,
                ItemTemplateId = 102,
                SourceSnapshot = CreateStackable(102, 2),
            });
            result.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = 65,
                ItemTemplateId = 1183,
                StackCount = 77,
                GrantedCount = 1,
                CoreSnapshot = CreateStackable(1183, 77, 0x6A8AC176),
            });

            var body = CompoundItemAckBuilder.Build(result);
            return body.Length == 69
                && body[0] == 1
                && body[1] == 3
                && body[2] == 0
                && ReadInt16(body, 3) == 4
                && ReadInt32(body, 5) == 10
                && body[9] == 0
                && ReadInt16(body, 10) == 359
                && ReadInt32(body, 12) == 100
                && body[16] == 0
                && ReadInt16(body, 17) == 3
                && ReadInt32(body, 19) == 1
                && body[23] == 1
                && body[24] == 0
                && ReadInt16(body, 25) == 65
                && ReadInt32(body, 27) == 1183
                && ReadInt32(body, 31) == 1
                && IsZeroRange(body, 35, 34);
        }

        private static bool VerifyEquipmentRewardAck()
        {
            var equipment = ItemCore.Create(ItemCore.KindEquipment, 100300221);
            equipment.Value = 0x12345678;
            equipment.Attr = 0x2A;
            equipment.Durability = 1234;
            equipment.SealFlag = 1;
            equipment.AmplifyType = 0x80;
            equipment.AmplifyValue = 77;
            equipment.Marker16 = 0x01020304;
            equipment.GenuineUpgrade = 3;
            equipment.EquipmentLockId = 9;

            var result = new CompoundItemRecipeResult();
            result.DeletedEntries.Add(new CompoundItemDeletedEntry
            {
                ListType = InventoryListType.Main,
                SlotIndex = 9,
                Count = 1,
                RemainingCount = 0,
                ItemTemplateId = 100300220,
                SourceSnapshot = ItemCore.Create(ItemCore.KindEquipment, 100300220),
            });
            result.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = 9,
                ItemTemplateId = equipment.ItemId,
                StackCount = 1,
                GrantedCount = 1,
                Durability = equipment.Durability,
                Attr = equipment.Attr,
                CoreSnapshot = equipment.Copy(),
            });

            var body = CompoundItemAckBuilder.Build(result);
            return body.Length == 55
                && body[0] == 1
                && body[1] == 1
                && body[2] == 1
                && ReadInt16(body, 3) == 9
                && ReadInt32(body, 5) == 1
                && body[9] == 1
                && body[10] == ItemCore.KindEquipment
                && ReadInt16(body, 11) == 9
                && ReadInt32(body, 13) == equipment.ItemId
                && ReadInt32(body, 17) == equipment.Value
                && IsZeroRange(body, 21, 34);
        }

        private static bool VerifyMultiRewardEntryStride()
        {
            var result = new CompoundItemRecipeResult();
            result.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = 4,
                ItemTemplateId = 1183,
                StackCount = 3,
                GrantedCount = 3,
                CoreSnapshot = CreateStackable(1183, 3),
            });
            result.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = 12,
                ItemTemplateId = 2205,
                StackCount = 1,
                GrantedCount = 1,
                CoreSnapshot = CreateStackable(2205, 1),
            });

            var body = CompoundItemAckBuilder.Build(result);
            return body.Length == 81
                && body[0] == 1
                && body[1] == 0
                && body[2] == 2
                && body[3] == 0
                && ReadInt16(body, 4) == 4
                && ReadInt32(body, 6) == 1183
                && ReadInt32(body, 10) == 3
                && IsZeroRange(body, 14, 22)
                && body[36] == 0
                && ReadInt16(body, 37) == 12
                && ReadInt32(body, 39) == 2205
                && ReadInt32(body, 43) == 1
                && IsZeroRange(body, 47, 34);
        }

        private static ItemCore CreateStackable(int itemId, int count, int marker16 = ItemCore.Marker16Default)
        {
            var core = ItemCore.Create(ItemCore.KindConsumable, itemId);
            core.Count = count;
            core.Marker16 = marker16;
            return core;
        }

        private static short ReadInt16(byte[] data, int offset)
            => BitConverter.ToInt16(data, offset);

        private static int ReadInt32(byte[] data, int offset)
            => BitConverter.ToInt32(data, offset);

        private static bool IsZeroRange(byte[] data, int offset, int length)
        {
            for (var index = 0; index < length; index++)
            {
                if (data[offset + index] != 0)
                    return false;
            }

            return true;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
