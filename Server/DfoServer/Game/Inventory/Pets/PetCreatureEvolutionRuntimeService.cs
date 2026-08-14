using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureEvolutionRuntimeService
    {
        private static readonly Lazy<PetCreatureEvolutionCatalog> CatalogCache =
            new Lazy<PetCreatureEvolutionCatalog>(PetCreatureEvolutionCatalog.Load);

        internal static PetCreatureEvolutionResult TryEvolveEquippedPetCreature(
            InventoryService inventory,
            int creatureKey,
            int afterLevel)
        {
            if (inventory == null || creatureKey <= 0 || afterLevel <= 0)
                return PetCreatureEvolutionResult.Noop;

            var equipped = inventory.GetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot);
            if (equipped == null
                || equipped.ItemKind != ItemCore.KindCreature
                || equipped.Value != creatureKey)
            {
                return PetCreatureEvolutionResult.Noop;
            }

            var catalog = CatalogCache.Value;
            if (!catalog.TryResolveByItemId(equipped.ItemId, out var current))
                return PetCreatureEvolutionResult.Noop;

            if (!current.CanAutoEvolve || afterLevel < current.EvolutionLevel)
                return PetCreatureEvolutionResult.Noop;

            if (current.EvolutionItemTemplateId <= 0
                || !catalog.TryResolveByItemId(current.EvolutionItemTemplateId, out var next)
                || next.ItemTemplateId <= 0
                || next.ItemTemplateId == equipped.ItemId)
            {
                FileLogger.Log($"[PetCreatureEvolution] skipped: missing target currentCreature={current.CreatureId} targetCreature={current.EvolutionCreatureId} targetItem=0x{current.EvolutionItemTemplateId:X8} item=0x{equipped.ItemId:X8}");
                return PetCreatureEvolutionResult.Noop;
            }

            var updated = equipped.Copy();
            updated.ItemId = next.ItemTemplateId;
            if (!inventory.SetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot, updated))
                return PetCreatureEvolutionResult.Noop;

            FileLogger.Log($"[PetCreatureEvolution] evolved cid={inventory.CharacterId} key={creatureKey} creature={current.CreatureId}->{next.CreatureId} item=0x{equipped.ItemId:X8}->0x{next.ItemTemplateId:X8} level={afterLevel}");
            return new PetCreatureEvolutionResult(
                changed: true,
                creatureKey: creatureKey,
                currentCreatureId: current.CreatureId,
                evolvedCreatureId: next.CreatureId,
                evolvedCreatureParam: next.CreatureParam,
                previousItemTemplateId: equipped.ItemId,
                evolvedItemTemplateId: next.ItemTemplateId,
                equipmentSlot: PetInventoryLayout.CreatureEquipSlot);
        }

        internal static PetCreatureEvolutionResult TryCompletePetCreatureEvolutionQuest(
            InventoryService inventory,
            int requiredCreatureId,
            int requiredLevel,
            int targetCreatureId)
        {
            if (inventory == null || requiredCreatureId <= 0 || targetCreatureId <= 0)
                return PetCreatureEvolutionResult.Noop;

            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out var equipped, out var detail))
                return PetCreatureEvolutionResult.Noop;

            var catalog = CatalogCache.Value;
            if (!catalog.TryResolveByItemId(equipped.ItemId, out var current))
                return PetCreatureEvolutionResult.Noop;

            if (!current.HasEvolutionQuest || current.CreatureId != requiredCreatureId)
            {
                FileLogger.Log($"[PetCreatureEvolution] quest skipped: current mismatch cid={inventory.CharacterId} current={current.CreatureId} required={requiredCreatureId} hasQuest={current.HasEvolutionQuest}");
                return PetCreatureEvolutionResult.Noop;
            }

            var minLevel = Math.Max(requiredLevel, current.EvolutionLevel);
            if (minLevel > 0 && detail.Level < minLevel)
            {
                FileLogger.Log($"[PetCreatureEvolution] quest skipped: level too low cid={inventory.CharacterId} creature={current.CreatureId} level={detail.Level} required={minLevel}");
                return PetCreatureEvolutionResult.Noop;
            }

            if (current.EvolutionCreatureId != targetCreatureId
                || !catalog.TryResolveByCreatureId(targetCreatureId, out var next)
                || next.ItemTemplateId <= 0
                || next.ItemTemplateId == equipped.ItemId)
            {
                FileLogger.Log($"[PetCreatureEvolution] quest skipped: target mismatch cid={inventory.CharacterId} current={current.CreatureId} expected={current.EvolutionCreatureId} reward={targetCreatureId}");
                return PetCreatureEvolutionResult.Noop;
            }

            var updated = equipped.Copy();
            updated.ItemId = next.ItemTemplateId;
            if (!inventory.SetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot, updated))
                return PetCreatureEvolutionResult.Noop;

            FileLogger.Log($"[PetCreatureEvolution] quest evolved cid={inventory.CharacterId} key={detail.Uid} creature={current.CreatureId}->{next.CreatureId} item=0x{equipped.ItemId:X8}->0x{next.ItemTemplateId:X8} level={detail.Level}");
            return new PetCreatureEvolutionResult(
                changed: true,
                creatureKey: detail.Uid,
                currentCreatureId: current.CreatureId,
                evolvedCreatureId: next.CreatureId,
                evolvedCreatureParam: next.CreatureParam,
                previousItemTemplateId: equipped.ItemId,
                evolvedItemTemplateId: next.ItemTemplateId,
                equipmentSlot: PetInventoryLayout.CreatureEquipSlot);
        }

        internal static HashSet<int> LoadEligiblePetCreatureEvolutionQuestKinds(InventoryService inventory)
        {
            var result = new HashSet<int>();
            if (!TryLoadEquippedQuestState(inventory, out var state))
                return result;

            result.Add(state.CreatureId);
            return result;
        }

        private static bool TryLoadEquippedQuestState(
            InventoryService inventory,
            out PetCreatureEvolutionQuestState state)
        {
            state = default(PetCreatureEvolutionQuestState);
            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out var equipped, out var detail))
                return false;

            var catalog = CatalogCache.Value;
            if (!catalog.TryResolveByItemId(equipped.ItemId, out var current))
                return false;

            if (current.EvolutionLevel <= 0
                || current.EvolutionItemTemplateId <= 0
                || !current.HasEvolutionQuest
                || detail.Level < current.EvolutionLevel)
                return false;

            state = new PetCreatureEvolutionQuestState(
                current.CreatureId,
                current.EvolutionCreatureId,
                current.EvolutionItemTemplateId,
                current.EvolutionLevel);
            return true;
        }
    }
}
