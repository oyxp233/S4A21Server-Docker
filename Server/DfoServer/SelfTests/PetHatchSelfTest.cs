using DfoServer.Game.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class PetHatchSelfTest
    {
        private const int AccountId = 163003;
        private const int CharacterId = 163003;
        private const short EggSlot = 48;
        private const int BoboEggItemId = 0x0000F62E;
        private const int BoboPetItemId = 0x0000F62F;
        private const int PetSerial = 123;

        public static int Run()
        {
            Console.WriteLine("=== PET_HATCH selftest ===");

            var failures = 0;
            Check("pet egg is pet inventory equipment",
                ItemMetadataResolver.IsPetInventoryEquipment(BoboEggItemId),
                ref failures);
            Check("pet egg PVF output index resolves hatched creature",
                CreatureEggResolver.TryResolveHatchedCreatureItemId(BoboEggItemId, out var outputItemId)
                && outputItemId == BoboPetItemId,
                ref failures);

            var inventory = new InventoryService(CharacterId, AccountId);
            var egg = ItemCore.Create(ItemCore.KindCreature, BoboEggItemId);
            egg.Value = PetSerial;
            inventory.SetItem(InventoryListType.Pet, EggSlot, egg);

            Check("hatching pet egg succeeds",
                PetCreatureEggService.TryHatchCreatureEgg(inventory, InventoryListType.Pet, EggSlot, BoboEggItemId, out var result)
                && result != null
                && result.SlotIndex == EggSlot
                && result.EggItemTemplateId == BoboEggItemId
                && result.HatchedItemTemplateId == BoboPetItemId
                && result.PetSerialOrHandle == PetSerial,
                ref failures);

            var hatched = inventory.GetItem(InventoryListType.Pet, EggSlot);
            Check("pet egg row remains in same slot", hatched != null, ref failures);
            Check("pet egg row changed to hatched pet", hatched != null && hatched.ItemId == BoboPetItemId, ref failures);
            Check("hatched pet keeps serial", hatched != null && hatched.Value == PetSerial, ref failures);
            Check("hatched pet has creature-list detail",
                inventory.CreatureDetails.TryGetDetail(PetSerial, out var detail)
                && detail != null
                && detail.Level == 1,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
