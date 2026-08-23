using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class A21EquipmentDurabilitySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_EQUIPMENT_DURABILITY selftest ===");
            var failures = 0;

            Check(
                "DECREASE_DURABILITY parses one-byte A21 equipment slot",
                DecreaseDurabilityRequest.TryParse(
                    new[] { (byte)EquipmentType.Weapon },
                    out var request)
                && request.EquipmentSlotIndex == (short)EquipmentType.Weapon,
                ref failures);

            Check(
                "DECREASE_DURABILITY rejects unexpected body length",
                !DecreaseDurabilityRequest.TryParse(Array.Empty<byte>(), out _)
                && !DecreaseDurabilityRequest.TryParse(
                    new[] { (byte)EquipmentType.Weapon, (byte)0 },
                    out _),
                ref failures);

            var inventory = new InventoryService(1, 1);
            inventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.Weapon,
                new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = 1001,
                    Durability = 2,
                });

            Check(
                "weapon durability decreases by one",
                InventoryDurabilityService.TryDecreaseEquippedDurability(
                    inventory,
                    (short)EquipmentType.Weapon,
                    out var first)
                && first.Changed
                && first.PreviousDurability == 2
                && first.CurrentDurability == 1
                && inventory.GetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Weapon).Durability == 1,
                ref failures);

            Check(
                "weapon durability clamps at zero",
                InventoryDurabilityService.TryDecreaseEquippedDurability(
                    inventory,
                    (short)EquipmentType.Weapon,
                    out var second)
                && second.Changed
                && second.PreviousDurability == 1
                && second.CurrentDurability == 0
                && InventoryDurabilityService.TryDecreaseEquippedDurability(
                    inventory,
                    (short)EquipmentType.Weapon,
                    out var third)
                && !third.Changed
                && third.CurrentDurability == 0
                && inventory.GetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Weapon).Durability == 0,
                ref failures);

            inventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.Weapon,
                new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = 1001,
                    Durability = 2,
                });

            Check(
                "old A12 weapon slot 11 does not alias A21 weapon slot 12",
                InventoryDurabilityService.TryDecreaseEquippedDurability(
                    inventory,
                    11,
                    out var oldSlot)
                && !oldSlot.Changed
                && inventory.GetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Weapon).Durability == 2,
                ref failures);

            inventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.Amulet,
                new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment,
                    ItemId = 2001,
                    Durability = 2,
                });
            Check(
                "accessory slot is ignored by durability command",
                InventoryDurabilityService.TryDecreaseEquippedDurability(
                    inventory,
                    (short)EquipmentType.Amulet,
                    out var accessory)
                && !accessory.Changed
                && inventory.GetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Amulet).Durability == 2,
                ref failures);

            Check(
                "DECREASE_DURABILITY ACK carries status and slot",
                BytesEqual(
                    DecreaseDurabilityAckBuilder.BuildSuccess(
                        (short)EquipmentType.Shoulder),
                    new byte[] { 0x01, 0x0F }),
                ref failures);

            Check(
                "DECREASE_DURABILITY error ACK uses command error shape",
                BytesEqual(
                    DecreaseDurabilityAckBuilder.BuildError(
                        DecreaseDurabilityAckBuilder.ErrorInvalidTarget),
                    new byte[] { 0x00, 0x11 }),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_EQUIPMENT_DURABILITY selftest passed."
                    : $"A21_EQUIPMENT_DURABILITY selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool BytesEqual(byte[] actual, byte[] expected)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
                return false;

            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                    return false;
            }

            return true;
        }
    }
}
