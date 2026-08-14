using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    public interface IMercenaryAvatarBonusTierProvider
    {
        int ResolveTier(int characterId, int nowUnixSeconds, MercenaryConfig config);
    }

    public sealed class MercenaryAvatarBonusTierProvider : IMercenaryAvatarBonusTierProvider
    {
        private const int RequiredAvatarSlotCount = 8;
        private const int MaximumRewardAvatarGrade = 3;
        private readonly string _connectionString;

        public MercenaryAvatarBonusTierProvider(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public MercenaryAvatarBonusTierProvider(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public int ResolveTier(int characterId, int nowUnixSeconds, MercenaryConfig config)
        {
            if (characterId <= 0 || config == null)
                return 0;

            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    return ResolveTier(lease.Inventory, nowUnixSeconds, config);
            }

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var equippedItems = InventoryItemRepository.LoadEquippedItems(connection, characterId);
                var avatarDetails = AvatarDetailRepository.LoadForCharacter(connection, characterId);
                return ResolveTier(equippedItems, avatarDetails, nowUnixSeconds, config);
            }
        }

        private static int ResolveTier(InventoryService inventory, int nowUnixSeconds, MercenaryConfig config)
        {
            var found = 0;
            var minimumGrade = 10;
            for (short slot = 0; slot < RequiredAvatarSlotCount; slot++)
            {
                if (!inventory.TryGetItem(InventoryListType.Equipment, slot, out var core)
                    || !TryApplyAvatar(core, inventory.AvatarDetails.GetDetail(core.AvatarUid), nowUnixSeconds, ref minimumGrade))
                    return 0;
                found++;
            }

            return ResolveOfficialTier(found, minimumGrade, config);
        }

        private static int ResolveTier(
            IReadOnlyList<InventoryItem> equippedItems,
            IReadOnlyDictionary<long, AvatarDetail> avatarDetails,
            int nowUnixSeconds,
            MercenaryConfig config)
        {
            var found = 0;
            var minimumGrade = 10;
            foreach (var item in equippedItems)
            {
                if (item.SlotIndex < 0 || item.SlotIndex >= RequiredAvatarSlotCount)
                    continue;
                if (item.SlotIndex != found
                    || !avatarDetails.TryGetValue(item.Core?.AvatarUid ?? 0, out var detail)
                    || !TryApplyAvatar(item.Core, detail, nowUnixSeconds, ref minimumGrade))
                    return 0;
                found++;
            }

            return ResolveOfficialTier(found, minimumGrade, config);
        }

        private static bool TryApplyAvatar(
            ItemCore core,
            AvatarDetail detail,
            int nowUnixSeconds,
            ref int minimumGrade)
        {
            if (core == null
                || core.ItemKind != ItemCore.KindAvatar
                || core.ItemId <= 0
                || core.AvatarUid <= 0
                || detail == null
                || detail.ItemId != core.ItemId
                || (detail.ExpireDate > 0 && detail.ExpireDate <= nowUnixSeconds))
                return false;

            var grade = ItemMetadataResolver.Resolve(core.ItemId).Rarity;
            if (grade < 0)
                return false;

            minimumGrade = Math.Min(minimumGrade, grade);
            return true;
        }

        internal static int ResolveOfficialTier(int foundSlotCount, int minimumGrade, MercenaryConfig config)
        {
            return foundSlotCount == RequiredAvatarSlotCount
                && minimumGrade <= MaximumRewardAvatarGrade
                ? config.ClampAvatarTier(minimumGrade)
                : 0;
        }
    }
}
