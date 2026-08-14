using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using PvfLib;

namespace DfoServer.Network.Builders
{
    public sealed class CollectionBoxBodyBuilder : IInitPacketBuilder
    {
        private readonly CollectBoxProgressRepository _progressRepository;

        public CollectionBoxBodyBuilder(CollectBoxProgressRepository progressRepository)
        {
            _progressRepository = progressRepository;
        }

        public ushort NotiType => 0x0381;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var indexes = CollectBoxDataService.GetAllIndexes();
            if (occurrenceIndex < 0 || occurrenceIndex >= indexes.Count)
            {
                body = Array.Empty<byte>();
                return false;
            }

            var characterId = snapshot.CharacterRecord?.CharacterId ?? 0;
            return TryBuildForBox(_progressRepository, characterId, indexes[occurrenceIndex], out body);
        }

        public static bool TryBuildForBox(
            CollectBoxProgressRepository progressRepository,
            int characterId,
            int boxIndex,
            out byte[] body)
        {
            var entry = CollectBoxDataService.GetByIndex(boxIndex);
            if (entry == null)
            {
                body = Array.Empty<byte>();
                return false;
            }

            var savedSlots = characterId > 0 && progressRepository != null
                ? progressRepository.LoadSlots(characterId, entry.Index)
                : Array.Empty<CollectBoxSlotEntry>();
            return BuildForBox(entry, savedSlots, out body);
        }

        internal static bool TryBuildForBox(CollectBoxModel model, int boxIndex, out byte[] body)
        {
            var entry = CollectBoxDataService.GetByIndex(boxIndex);
            if (entry == null)
            {
                body = Array.Empty<byte>();
                return false;
            }

            var savedSlots = model != null
                ? model.GetSlots(entry.Index)
                : Array.Empty<CollectBoxSlotEntry>();
            return BuildForBox(entry, savedSlots, out body);
        }

        private static bool BuildForBox(
            CollectBoxEntry entry,
            IReadOnlyList<CollectBoxSlotEntry> savedSlots,
            out byte[] body)
        {
            uint remainingSeconds = 0;
            byte statusFlags = 1;
            if (!string.IsNullOrEmpty(entry.MaxExpirationDate) &&
                DateTime.TryParse(entry.MaxExpirationDate, out var maxExpire))
            {
                var remaining = maxExpire - DateTime.Now;
                if (remaining.TotalSeconds > 0)
                {
                    remainingSeconds = (uint)remaining.TotalSeconds;
                    statusFlags = 0;
                }
                else
                {
                    remainingSeconds = 0xFFFFFFFF;
                    statusFlags = 0;
                }
            }

            var itemCount = savedSlots != null ? savedSlots.Count : 0;
            body = new byte[8 + itemCount * 4];
            body[0] = (byte)entry.Index;
            body[1] = 1;
            Buffer.BlockCopy(BitConverter.GetBytes(remainingSeconds), 0, body, 2, 4);
            body[6] = statusFlags;
            body[7] = (byte)itemCount;
            for (var i = 0; i < itemCount; i++)
                Buffer.BlockCopy(BitConverter.GetBytes((uint)savedSlots[i].ItemId), 0, body, 8 + i * 4, 4);

            return true;
        }
    }
}
