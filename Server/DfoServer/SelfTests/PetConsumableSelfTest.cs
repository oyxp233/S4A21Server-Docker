using System;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class PetConsumableSelfTest
    {
        private const int AccountId = 163001;
        private const int CharacterId = 163001;
        private const short PetFoodSlot = 189;
        private const short RenameCardSlot = 190;
        private const int PetFoodItemTemplateId = 24;
        private const int RenameCardItemTemplateId = 25;
        private const int EquippedPetItemTemplateId = 100330649;
        private const int InitialPetFoodCount = 999;
        private const int InitialRenameCardCount = 5;
        private const int PetCreatureKey = 1;
        private const int OtherPetCreatureKey = 2;
        private const int InitialPetSatiety = 40;
        private const int InitialOtherPetSatiety = 10;
        private const int InitialPetProgressValue = 1234;
        private const int PetFoodSatietyDelta = 30;

        public static int Run()
        {
            Console.WriteLine("=== PET_CONSUMABLE selftest ===");

            var failures = 0;
            Check("cera-shop 1000-count stack is not truncated to 999",
                InventoryCeraShopStackPolicy.NormalizeEffectiveStackCount(1, 1000, 1000) == 1000,
                ref failures);
            Check("cera-shop product count is honored when PVF stack limit is missing",
                InventoryCeraShopStackPolicy.NormalizeEffectiveStackCount(1, 1000, 0) == 1000,
                ref failures);
            Check("cera-shop missing PVF stack limit stays uncapped",
                InventoryCeraShopStackPolicy.ResolveStackLimit(1, 0) == 0,
                ref failures);
            Check("cera-shop missing PVF stack limit allows repeated stacks past 999",
                InventoryCeraShopStackPolicy.NormalizeEffectiveStackCount(1000, 1, 0) == 1000,
                ref failures);
            CheckUseStackableProtocolPlan(ref failures);
            CheckCreatureStateProtocolBody(ref failures);

            var inventory = CreateInventoryWithEquippedPet(InitialPetSatiety);
            Check("using one pet consumable succeeds",
                PetConsumableService.TryUsePetConsumable(
                    inventory,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out var result),
                ref failures);
            if (result != null)
            {
                Check("pet consumable result remains in pet list", result.ListType == InventoryListType.Pet, ref failures);
                Check("pet consumable result keeps source slot", result.SlotIndex == PetFoodSlot, ref failures);
                Check("pet consumable remaining count is decremented", result.RemainingStackCount == InitialPetFoodCount - 1, ref failures);
                Check("pet consumable instance mirrors remaining count", result.InstanceValue == InitialPetFoodCount - 1, ref failures);
                Check("pet consumable applied count is one", result.AppliedCount == 1, ref failures);
                Check("pet consumable reports satiety sync", result.PetSatietyChanged, ref failures);
                Check("pet consumable targets equipped creature key", result.PetCreatureKey == PetCreatureKey, ref failures);
            }

            var remainingFood = inventory.GetItem(InventoryListType.Pet, PetFoodSlot);
            Check("pet consumable item still exists after single use",
                remainingFood != null && remainingFood.Count == InitialPetFoodCount - 1,
                ref failures);
            Check("pet food raises equipped creature satiety by PVF feed amount",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == InitialPetSatiety + PetFoodSatietyDelta,
                ref failures);
            Check("pet food does not raise inactive creature satiety",
                inventory.CreatureDetails.GetDetail(OtherPetCreatureKey)?.Stomach == InitialOtherPetSatiety,
                ref failures);
            Check("pet food does not change creature progress value",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.ProgressValue32 == InitialPetProgressValue,
                ref failures);

            Check("non-feed pet consumable succeeds",
                PetConsumableService.TryUsePetConsumable(
                    inventory,
                    InventoryListType.Pet,
                    RenameCardSlot,
                    RenameCardItemTemplateId,
                    out var renameResult),
                ref failures);
            if (renameResult != null)
                Check("non-feed pet consumable decrements",
                    renameResult.RemainingStackCount == InitialRenameCardCount - 1,
                    ref failures);
            Check("non-feed pet consumable does not raise creature satiety",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == InitialPetSatiety + PetFoodSatietyDelta,
                ref failures);
            Check("non-feed pet consumable does not change creature progress value",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.ProgressValue32 == InitialPetProgressValue,
                ref failures);

            var clampInventory = CreateInventoryWithEquippedPet(90);
            Check("pet food use at high satiety succeeds",
                PetConsumableService.TryUsePetConsumable(
                    clampInventory,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out _),
                ref failures);
            Check("pet food use clamps equipped creature satiety to 100",
                clampInventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == 100,
                ref failures);

            var noActiveInventory = CreateInventoryWithoutEquippedPet();
            Check("no-active pet consumable succeeds",
                PetConsumableService.TryUsePetConsumable(
                    noActiveInventory,
                    InventoryListType.Pet,
                    PetFoodSlot,
                    PetFoodItemTemplateId,
                    out var noActiveResult),
                ref failures);
            if (noActiveResult != null)
                Check("no-active pet consumable does not report satiety sync",
                    !noActiveResult.PetSatietyChanged,
                    ref failures);
            Check("no-active pet food does not raise first creature satiety",
                noActiveInventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == InitialPetSatiety,
                ref failures);
            Check("no-active pet food does not raise second creature satiety",
                noActiveInventory.CreatureDetails.GetDetail(OtherPetCreatureKey)?.Stomach == InitialOtherPetSatiety,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static InventoryService CreateInventoryWithEquippedPet(int satiety)
        {
            var inventory = CreateInventoryWithoutEquippedPet();
            var equippedPet = ItemCore.Create(ItemCore.KindCreature, EquippedPetItemTemplateId);
            equippedPet.Value = PetCreatureKey;
            inventory.SetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot, equippedPet);
            inventory.CreatureDetails.GetDetail(PetCreatureKey).Stomach = (byte)Math.Max(0, Math.Min(100, satiety));
            return inventory;
        }

        private static InventoryService CreateInventoryWithoutEquippedPet()
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            var food = ItemCore.Create(ItemCore.KindCreatureConsumable, PetFoodItemTemplateId);
            food.Count = InitialPetFoodCount;
            inventory.SetItem(InventoryListType.Pet, PetFoodSlot, food);

            var renameCard = ItemCore.Create(ItemCore.KindCreatureConsumable, RenameCardItemTemplateId);
            renameCard.Count = InitialRenameCardCount;
            inventory.SetItem(InventoryListType.Pet, RenameCardSlot, renameCard);

            inventory.CreatureDetails.Put(new CreatureDetail
            {
                Uid = PetCreatureKey,
                Field04 = InitialPetSatiety,
                ModeFlag = 0,
                ProgressValue32 = InitialPetProgressValue,
                FieldAfterValue32 = 1,
            });
            inventory.CreatureDetails.Put(new CreatureDetail
            {
                Uid = OtherPetCreatureKey,
                Field04 = InitialOtherPetSatiety,
                ModeFlag = 0,
                ProgressValue32 = 0,
                FieldAfterValue32 = 1,
            });
            return inventory;
        }

        private static void CheckUseStackableProtocolPlan(ref int failures)
        {
            var consumedPetPlan = InventoryHandler.BuildUseStackableResponsePlan(
                consumed: true,
                result: new InventoryMutationResult
                {
                    ListType = InventoryListType.Pet,
                    SlotIndex = PetFoodSlot,
                    ItemTemplateId = PetFoodItemTemplateId,
                    RemainingStackCount = 0,
                    InstanceValue = 0,
                },
                listType: InventoryListType.Pet,
                slotIndex: PetFoodSlot,
                instanceValue: 1,
                itemCode: PetFoodItemTemplateId);
            Check("pet use success ACK uses 0x002C success", consumedPetPlan.AckBody.Length > 0 && consumedPetPlan.AckBody[0] == 0x01, ref failures);
            Check("pet use success does not send pet item-list update", consumedPetPlan.ItemListUpdateBody == null, ref failures);

            var stalePetPlan = InventoryHandler.BuildUseStackableResponsePlan(
                consumed: false,
                result: null,
                listType: InventoryListType.Pet,
                slotIndex: PetFoodSlot,
                instanceValue: 0,
                itemCode: PetFoodItemTemplateId);
            Check("stale pet use is acknowledged as success", stalePetPlan.AckBody.Length > 0 && stalePetPlan.AckBody[0] == 0x01, ref failures);
            Check("stale pet use does not send pet item-list update", stalePetPlan.ItemListUpdateBody == null, ref failures);

            var mainFailurePlan = InventoryHandler.BuildUseStackableResponsePlan(
                consumed: false,
                result: null,
                listType: InventoryListType.Main,
                slotIndex: PetFoodSlot,
                instanceValue: 0,
                itemCode: PetFoodItemTemplateId);
            Check("non-pet use failure still uses error ACK", mainFailurePlan.AckBody.Length > 0 && mainFailurePlan.AckBody[0] == 0x00, ref failures);
        }

        private static void CheckCreatureStateProtocolBody(ref int failures)
        {
            var entry = new CreatureItemEntrySnapshot
            {
                CreatureKey = 153,
                Field04 = 61,
                ModeFlag = 0,
                ProgressValue32 = 3294,
                FieldAfterValue32 = 61,
                CreatureTextBytes = Array.Empty<byte>(),
                TailFlag = 0x07,
            };

            var stateBody = CreatureListBodyBuilder.BuildCreatureStateBody(entry);
            Check("creature state refresh body omits creature-list count",
                stateBody.Length == 16 && BitConverter.ToInt32(stateBody, 0) == entry.CreatureKey,
                ref failures);
            Check("creature state refresh body carries satiety fields",
                stateBody[4] == entry.Field04 && stateBody[10] == entry.FieldAfterValue32,
                ref failures);

            var snapshot = new SelectCharacterDataSnapshot();
            snapshot.InitializationSnapshot.CreatureItemList.Entries.Add(entry);
            var hasListBody = new CreatureListBodyBuilder().TryBuild(snapshot, 0, out var listBody);
            Check("creature item-list body keeps leading count",
                hasListBody && listBody.Length == stateBody.Length + 1 && listBody[0] == 1,
                ref failures);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
