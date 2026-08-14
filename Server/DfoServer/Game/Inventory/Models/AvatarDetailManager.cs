using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class AvatarDetailManager
    {
        private Dictionary<long, AvatarDetail> _details = new Dictionary<long, AvatarDetail>();
        private readonly HashSet<long> _dirtyAvatarUids = new HashSet<long>();
        private readonly HashSet<long> _deletedAvatarUids = new HashSet<long>();
        private readonly IGameDatabase _database;

        internal AvatarDetailManager(IGameDatabase database = null)
        {
            _database = database;
        }

        public IReadOnlyCollection<AvatarDetail> Details => _details.Values;

        public IReadOnlyCollection<long> DirtyDetailUids => _dirtyAvatarUids;

        public IReadOnlyCollection<long> DeletedDetailUids => _deletedAvatarUids;

        public void LoadForCharacter(int characterId)
        {
            var database = _database ?? GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            {
                LoadForCharacter(connection, characterId);
            }
        }

        internal void LoadForCharacter(SqliteConnection connection, int characterId)
        {
            _details = AvatarDetailRepository.LoadForCharacter(connection, characterId);
            _dirtyAvatarUids.Clear();
            _deletedAvatarUids.Clear();
        }

        public AvatarDetail GetDetail(long avatarUid)
        {
            _details.TryGetValue(avatarUid, out var detail);
            return detail;
        }

        public bool TryGetDetail(long avatarUid, out AvatarDetail detail)
        {
            return _details.TryGetValue(avatarUid, out detail);
        }

        public void Attach(AvatarDetail detail)
        {
            if (detail == null || detail.AvatarUid <= 0)
                return;

            _deletedAvatarUids.Remove(detail.AvatarUid);
            _details[detail.AvatarUid] = detail;
        }

        public bool Put(AvatarDetail detail)
        {
            Attach(detail);
            var saved = InventoryPersistenceService.SaveAvatarDetailImmediately(
                detail,
                _database);
            if (saved && detail != null)
            {
                _dirtyAvatarUids.Remove(detail.AvatarUid);
                _deletedAvatarUids.Remove(detail.AvatarUid);
            }
            return saved;
        }

        public AvatarDetail CreateDetail(ItemCore core, int ownerId, int characterId)
        {
            return CreateDetail(core, ownerId, characterId, true);
        }

        internal AvatarDetail CreateDetail(
            ItemCore core,
            int ownerId,
            int characterId,
            bool persistImmediately)
        {
            if (core == null || core.ItemKind != ItemCore.KindAvatar || core.ItemId <= 0)
                return null;

            if (core.AvatarUid <= 0)
            {
                var avatarUid = AvatarDetailRepository.AllocateAvatarUid(_database);
                if (avatarUid <= 0 || avatarUid > int.MaxValue)
                    return null;

                core.AvatarUid = (int)avatarUid;
            }

            if (_details.TryGetValue(core.AvatarUid, out var existing))
                return existing;

            var detail = new AvatarDetail
            {
                AvatarUid = core.AvatarUid,
                OwnerId = ownerId,
                CharacterId = characterId,
                ItemId = core.ItemId,
                ExpireDate = ResolveAvatarExpireDate(core.ItemId),
                ClearAvatarId = 0,
                JewelSocketView = new JewelSocket(),
                Color1 = 0,
                Color2 = 0,
                DeleteDate = 0,
            };

            if (persistImmediately)
            {
                if (!Put(detail))
                    return null;
            }
            else
            {
                Attach(detail);
                MarkDirty(detail.AvatarUid);
            }
            return detail;
        }

        public bool Detach(long avatarUid)
        {
            if (avatarUid <= 0)
                return false;

            _dirtyAvatarUids.Remove(avatarUid);
            _deletedAvatarUids.Remove(avatarUid);
            return _details.Remove(avatarUid);
        }

        internal bool RemoveDirty(long avatarUid)
        {
            if (avatarUid <= 0)
                return false;

            _dirtyAvatarUids.Remove(avatarUid);
            _deletedAvatarUids.Add(avatarUid);
            return _details.Remove(avatarUid);
        }

        public bool Remove(long avatarUid)
        {
            if (avatarUid <= 0)
                return false;

            _dirtyAvatarUids.Remove(avatarUid);
            _deletedAvatarUids.Remove(avatarUid);
            var removed = _details.Remove(avatarUid);
            InventoryPersistenceService.DeleteAvatarDetailImmediately(
                avatarUid,
                _database);
            return removed;
        }

        internal void MarkDirty(long avatarUid)
        {
            if (avatarUid > 0)
            {
                _deletedAvatarUids.Remove(avatarUid);
                _dirtyAvatarUids.Add(avatarUid);
            }
        }

        internal IReadOnlyList<AvatarDetail> GetDirtyDetails()
        {
            var result = new List<AvatarDetail>();
            foreach (var avatarUid in _dirtyAvatarUids)
            {
                if (_details.TryGetValue(avatarUid, out var detail))
                    result.Add(detail);
            }

            return result;
        }

        internal void ClearDirtyState()
        {
            _dirtyAvatarUids.Clear();
            _deletedAvatarUids.Clear();
        }

        private static int ResolveAvatarExpireDate(int itemId)
        {
            if (!ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment)
                || !EquipmentExpirationPolicyResolver.TryResolve(equipment, out var policy))
                return 0;

            if (policy.UsablePeriodDays > 0)
                return PvfExpirationMetadata.AddDaysFromNow(policy.UsablePeriodDays);

            return policy.AbsoluteExpirationUnixTime;
        }
    }
}
