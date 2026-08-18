using DfoServer.Network.Handlers;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.SelfTests
{
    public static class A21CreateCharacterProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_CREATE_CHARACTER_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "A21 accepts the final supported job id 13",
                CharacterSelectHandler.IsSupportedA21CreateJob(13),
                ref failures);
            Check(
                "A21 keeps job ids above 13 rejected",
                !CharacterSelectHandler.IsSupportedA21CreateJob(14),
                ref failures);
            Check(
                "A21 keeps the existing job range accepted",
                CharacterSelectHandler.IsSupportedA21CreateJob(0)
                && CharacterSelectHandler.IsSupportedA21CreateJob(12),
                ref failures);

            Check(
                "A21 maps the PVF weapon token to equipment slot 12",
                InitialCharacterEquipment.TryGetSlotForPvfToken("[weapon]", out var weaponSlot)
                && weaponSlot == (short)EquipmentType.Weapon
                && ItemSlotBoundService.IsValidSlotForKind(
                    ItemCore.KindEquipment,
                    InventoryListType.Equipment,
                    weaponSlot,
                    ItemSlotBoundService.MainExpandStageFull),
                ref failures);
            Check(
                "A21 rejects the old A12 weapon slot 11 for equipment",
                !ItemSlotBoundService.IsValidSlotForKind(
                    ItemCore.KindEquipment,
                    InventoryListType.Equipment,
                    11,
                    ItemSlotBoundService.MainExpandStageFull),
                ref failures);
            Check(
                "A21 keeps charm slot 30 outside the equipment physical range",
                !ItemSlotBoundService.IsInItemSpacePhysicalRange(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Charm),
                ref failures);
            Check(
                "A21 maps guild medal slot 31 into the equipment physical range",
                ItemSlotBoundService.IsInItemSpacePhysicalRange(
                    InventoryListType.Equipment,
                    (short)EquipmentType.GuildMedal)
                && ItemSlotBoundService.IsValidSlotForKind(
                    ItemCore.KindGuildMedal,
                    InventoryListType.Equipment,
                    (short)EquipmentType.GuildMedal,
                    ItemSlotBoundService.MainExpandStageFull),
                ref failures);
            Check(
                "A21 keeps only guild medal slot 31 open in the equipment physical range",
                ItemSlotBoundService.IsInItemSpacePhysicalRange(
                    InventoryListType.Equipment,
                    new ItemSlotRange((short)EquipmentType.GuildMedal, (short)EquipmentType.GuildMedal))
                && !ItemSlotBoundService.IsInItemSpacePhysicalRange(
                    InventoryListType.Equipment,
                    new ItemSlotRange((short)EquipmentType.Charm, (short)EquipmentType.GuildMedal)),
                ref failures);

            var initialEquipment = InitialCharacterEquipment.Get(13);
            Check(
                "A21 job 13 exposes PVF initial equipment",
                initialEquipment != null && initialEquipment.Length > 0,
                ref failures);
            if (initialEquipment != null)
            {
                foreach (var entry in initialEquipment)
                {
                    var valid = ItemMetadataResolver.TryResolveItemKind(entry.itemId, out var itemKind)
                        && ItemSlotBoundService.IsValidSlotForKind(
                            itemKind,
                            InventoryListType.Equipment,
                            entry.slot,
                            ItemSlotBoundService.MainExpandStageFull);
                    Check(
                        $"A21 PVF initial item {entry.itemId} fits equipment slot {entry.slot}",
                        valid,
                        ref failures);
                }
            }

            Console.WriteLine(
                failures == 0
                    ? "A21_CREATE_CHARACTER_PROTOCOL selftest passed."
                    : $"A21_CREATE_CHARACTER_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
