using DfoServer.Game.Characters;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.KnightShield
{
    public sealed class KnightShieldService
    {
        private readonly KnightShieldDeckRepository _repository;
        private readonly object _mutationGate = new object();

        public KnightShieldService(KnightShieldDeckRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public KnightShieldDeckSnapshot Load(int characterId)
        {
            return _repository.Load(characterId);
        }

        public bool TryEquipMain(
            CharacterRecord character,
            int shieldItemId,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason)
                || !ValidateShield(character, shieldItemId, out rejectReason))
                return false;

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                values[KnightShieldDeckSnapshot.MainSlotIndex] = shieldItemId;
                for (var slotIndex = 1; slotIndex < values.Length; slotIndex++)
                {
                    if (values[slotIndex] == shieldItemId)
                        values[slotIndex] = 0;
                }

                snapshot = new KnightShieldDeckSnapshot(values);
                _repository.Save(character.CharacterId, snapshot);
            }
            return true;
        }

        public bool TryUnequipMain(
            CharacterRecord character,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                values[KnightShieldDeckSnapshot.MainSlotIndex] = 0;
                snapshot = new KnightShieldDeckSnapshot(values);
                _repository.Save(character.CharacterId, snapshot);
            }
            return true;
        }

        public bool TryMoveDeckSlot(
            CharacterRecord character,
            int sourceSlotIndex,
            int destinationSlotIndex,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;
            if (!IsDeckSlotIndex(sourceSlotIndex) || !IsDeckSlotIndex(destinationSlotIndex))
            {
                rejectReason = $"deck slot move is outside 0..{KnightShieldDeckSnapshot.SlotCount - 1}: "
                    + $"source={sourceSlotIndex} destination={destinationSlotIndex}";
                return false;
            }

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                if (values[sourceSlotIndex] == 0)
                {
                    rejectReason = $"source deck slot {sourceSlotIndex} is empty";
                    return false;
                }

                if (sourceSlotIndex != destinationSlotIndex)
                {
                    var sourceShieldItemId = values[sourceSlotIndex];
                    values[sourceSlotIndex] = values[destinationSlotIndex];
                    values[destinationSlotIndex] = sourceShieldItemId;
                    snapshot = new KnightShieldDeckSnapshot(values);
                    _repository.Save(character.CharacterId, snapshot);
                }
                else
                {
                    snapshot = new KnightShieldDeckSnapshot(values);
                }
            }

            rejectReason = null;
            return true;
        }

        public bool TrySaveDeck(
            CharacterRecord character,
            IReadOnlyList<int> shieldItemIds,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;
            if (shieldItemIds == null || shieldItemIds.Count != KnightShieldDeckSnapshot.SlotCount)
            {
                rejectReason = $"deck slot count must be {KnightShieldDeckSnapshot.SlotCount}";
                return false;
            }

            var seen = new HashSet<int>();
            for (var slotIndex = 0; slotIndex < shieldItemIds.Count; slotIndex++)
            {
                var shieldItemId = shieldItemIds[slotIndex];
                if (shieldItemId == 0)
                    continue;
                if (!ValidateShield(character, shieldItemId, out rejectReason))
                    return false;
                if (!seen.Add(shieldItemId))
                {
                    rejectReason = $"duplicate shield item {shieldItemId}";
                    return false;
                }
            }

            lock (_mutationGate)
            {
                snapshot = new KnightShieldDeckSnapshot(shieldItemIds);
                _repository.Save(character.CharacterId, snapshot);
            }
            rejectReason = null;
            return true;
        }

        private static bool ValidateCharacter(CharacterRecord character, out string rejectReason)
        {
            if (character == null || character.CharacterId <= 0)
            {
                rejectReason = "character is unavailable";
                return false;
            }
            if (!KnightShieldDataProvider.IsEligibleCharacter(character))
            {
                rejectReason = $"character is not a guardian: job={character.Job} grow={character.GrowType}";
                return false;
            }

            rejectReason = null;
            return true;
        }

        private static bool ValidateShield(
            CharacterRecord character,
            int shieldItemId,
            out string rejectReason)
        {
            if (!KnightShieldDataProvider.IsCatalogShield(character.GrowType, shieldItemId))
            {
                rejectReason =
                    $"item {shieldItemId} is not in guardian shield catalog for grow={character.GrowType}";
                return false;
            }

            rejectReason = null;
            return true;
        }

        private static bool IsDeckSlotIndex(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < KnightShieldDeckSnapshot.SlotCount;
        }
    }
}
