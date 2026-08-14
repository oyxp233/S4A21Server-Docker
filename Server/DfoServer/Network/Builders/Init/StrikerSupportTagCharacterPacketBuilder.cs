using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mercenary;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network.Builders
{
    // 从选中的支援角色构建动态 0x019F 标记角色记录。
    // 包体为 count:u16 + record；record 包含身份、82B 属性、装备、称号和活动技能页。
    // ComboIndex 不写入 record；尾部固定为 5B 空不透明段。
    public static class StrikerSupportTagCharacterPacketBuilder
    {
        private const ushort TagCharacterInfoNotiType = 0x019F;
        private const int StatBlobLength = 82;
        private const int LastDefaultAvatarSlot = 8;
        private const int LastClientEquipmentSlot = 29;
        public static bool TryBuildOwnerSupportBody(int activeCharacterId, out byte[] body)
        {
            return TryBuildOwnerSupportBody(
                activeCharacterId,
                GameDatabase.CreateDefault(),
                out body);
        }

        public static bool TryBuildOwnerSupportBody(
            int activeCharacterId,
            IGameDatabase database,
            out byte[] body)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            return TryBuildPersistedOwnerSupportBody(
                activeCharacterId,
                database,
                out body);
        }

        private static bool TryBuildPersistedOwnerSupportBody(
            int activeCharacterId,
            IGameDatabase database,
            out byte[] body)
        {
            body = null;
            if (activeCharacterId <= 0)
                return false;

            try
            {
                var state = new SqliteMercenarySupportRepository(database).LoadSlot(
                    activeCharacterId,
                    MercenarySupportState.SingletonStateKey);
                body = BuildOwnerMappedBodyCore(activeCharacterId, state, database);
                return body != null && body.Length > 2;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GameProtocol] MERCENARY/STRIKER 0x{TagCharacterInfoNotiType:X4}" +
                    $" build failed owner={activeCharacterId}: {ex.Message}");
                body = null;
                return false;
            }
        }

        public static byte[] BuildOwnerMappedBody(
            int activeCharacterId,
            MercenarySupportState state)
        {
            return BuildOwnerMappedBody(
                activeCharacterId,
                state,
                GameDatabase.CreateDefault());
        }

        public static byte[] BuildOwnerMappedBody(
            int activeCharacterId,
            MercenarySupportState state,
            IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            try
            {
                return BuildOwnerMappedBodyCore(activeCharacterId, state, database);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER town 0x{TagCharacterInfoNotiType:X4} build failed owner={activeCharacterId}: {ex.Message}");
                return null;
            }
        }

        private static byte[] BuildOwnerMappedBodyCore(
            int activeCharacterId,
            MercenarySupportState state,
            IGameDatabase database)
        {
            if (StrikerSkillDataProvider.GetMaxActiveSupportCount() != 1)
            {
                FileLogger.Log("[GameProtocol] MERCENARY/STRIKER unsupported PVF active-support count");
                return null;
            }
            if (!TryLoadAndValidateState(
                    activeCharacterId,
                    state,
                    database,
                    out var support,
                    out var reason))
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER state rejected owner={activeCharacterId}: {reason}");
                return null;
            }

            var snapshot = new SqliteSubtype1Repository(database)
                .Load(support.CharacterId);
            if (snapshot == null)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER state rejected owner={activeCharacterId}: support subtype1 missing cid={support.CharacterId}");
                return null;
            }
            ApplyOfflineInventoryProjection(
                snapshot,
                support.CharacterId,
                database);

            if (!TryBuildEquipmentList(snapshot, support, out var equipment, out reason))
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER equipment rejected owner={activeCharacterId} support={support.CharacterId}: {reason}");
                return null;
            }

            var skills = BuildSkillPage(
                state,
                support,
                snapshot.SkillTreeIndex,
                database);
            if (skills.Count == 0 || skills.Count > byte.MaxValue)
            {
                FileLogger.Log($"[GameProtocol] MERCENARY/STRIKER skill page rejected owner={activeCharacterId} support={support.CharacterId}: count={skills.Count}");
                return null;
            }

            var record = BuildRecord(
                checked((ushort)activeCharacterId),
                support.Name,
                support.Level,
                support.Job,
                support.GrowType,
                state.SkillId,
                snapshot,
                equipment,
                skills);

            var writer = new GamePacketWriter();
            writer.WriteUInt16(1);
            writer.WriteBytes(record);
            return writer.ToArray();
        }

        private static byte[] BuildRecord(
            ushort mappedCharacterId,
            byte[] supportName,
            byte level,
            byte job,
            byte packedGrowContext,
            ushort selectedSkillId,
            UserInfoAdditionSnapshot snapshot,
            IReadOnlyList<EquippedEntrySnapshot> equipment,
            IReadOnlyList<SkillInfoEntrySnapshot> skills)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (equipment == null || equipment.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(equipment));
            if (skills == null || skills.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(skills));

            var statBlob = BuildStatBlob(snapshot);
            if (statBlob.Length != StatBlobLength)
                throw new InvalidOperationException($"unexpected striker stat blob length {statBlob.Length}");

            var writer = new GamePacketWriter();
            writer.WriteUInt16(mappedCharacterId);
            writer.WriteDstr(supportName);
            writer.WriteByte(level);
            writer.WriteByte(job);
            // 客户端分别读取 grow 的低四位和觉醒阶段位。
            writer.WriteByte(packedGrowContext);
            writer.WriteUInt16(selectedSkillId);
            writer.WriteUInt32((uint)statBlob.Length);
            writer.WriteBytes(statBlob);
            writer.WriteByte(checked((byte)equipment.Count));
            foreach (var item in equipment)
            {
                ItemListProtocolWriter.WriteNoti2EquippedEntry(
                    writer,
                    item.Slot,
                    item.Core,
                    snapshot.GetAvatarDetail(item.Core),
                    snapshot.GetCreatureDetail(item.Core));
            }
            writer.WriteUInt32(snapshot.CloneTitleItemId);
            writer.WriteByte(checked((byte)skills.Count));
            writer.WriteByte(snapshot.SkillTreeIndex);
            foreach (var skill in skills)
            {
                writer.WriteByte(skill.Slot);
                writer.WriteUInt16(skill.SkillId);
                writer.WriteByte(skill.Level);
            }
            // 每条 record 末尾固定写入空 opaque section。
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            return writer.ToArray();
        }

        private static byte[] BuildStatBlob(UserInfoAdditionSnapshot a)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(a.StatHpMax);
            writer.WriteUInt32(a.StatMpMax);
            writer.WriteInt16(a.StatPhysicalAttack);
            writer.WriteInt16(a.StatPhysicalDefense);
            writer.WriteInt16(a.StatMagicalAttack);
            writer.WriteInt16(a.StatMagicalDefense);
            writer.WriteInt16(a.StatFireResistance);
            writer.WriteInt16(a.StatWaterResistance);
            writer.WriteInt16(a.StatDarkResistance);
            writer.WriteInt16(a.StatLightResistance);
            // 34B 中段按已确认边界保留全零。
            for (var i = 0; i < 17; i++)
                writer.WriteUInt16(0);
            writer.WriteUInt32(a.StatInventoryLimit);
            writer.WriteUInt16(a.StatHpRegenSpeed);
            writer.WriteUInt16(a.StatMpRegenSpeed);
            writer.WriteUInt32(a.StatMoveSpeed);
            writer.WriteUInt16(a.StatAttackSpeed);
            writer.WriteUInt16(a.StatCastSpeed);
            writer.WriteUInt16(a.StatHitRecovery);
            writer.WriteUInt16(a.StatJumpPower);
            writer.WriteUInt32(a.StatWeight);
            return writer.ToArray();
        }

        private static List<SkillInfoEntrySnapshot> BuildSkillPage(
            MercenarySupportState state,
            CharacterSummary support,
            byte skillTreeIndex,
            IGameDatabase database)
        {
            var learned = StrikerSupportSkillLevelSource.LoadActiveSkillPageEntries(
                support.CharacterId,
                database,
                skillTreeIndex);

            // 只发送真实活动技能页，不为未学习的选中技能伪造槽位或等级。

            return NormalizeSkillPageEntries(state, learned);
        }

        private static void ApplyOfflineInventoryProjection(
            UserInfoAdditionSnapshot snapshot,
            int characterId,
            IGameDatabase database)
        {
            if (snapshot == null || characterId <= 0)
                return;

            using (var conn = database.OpenConnection())
            {
                var equippedItems = InventoryItemRepository.LoadEquippedItems(conn, characterId);
                var avatarDetails = AvatarDetailRepository.LoadForCharacter(conn, characterId);
                var creatureDetails = CreatureDetailRepository.LoadForCharacter(conn, characterId);
                var projection = new Noti2InventoryProjectionBuilder()
                    .BuildUserInfoAddition(equippedItems, avatarDetails, creatureDetails);

                var existingSlots = new HashSet<short>(snapshot.EquippedEntries.Select(entry => entry.Slot));
                foreach (var entry in projection.EquippedEntries)
                {
                    if (existingSlots.Add(entry.Slot))
                        snapshot.EquippedEntries.Add(entry);
                }
                foreach (var pair in projection.AvatarDetails)
                    snapshot.AvatarDetails[pair.Key] = pair.Value;
                foreach (var pair in projection.CreatureDetails)
                    snapshot.CreatureDetails[pair.Key] = pair.Value;
            }
        }

        private static List<SkillInfoEntrySnapshot> NormalizeSkillPageEntries(
            MercenarySupportState state,
            IReadOnlyList<SkillInfoEntrySnapshot> learned)
        {
            var result = learned
                .GroupBy(e => e.Slot)
                .Select(group => group.FirstOrDefault(e => e.SkillId == state.SkillId)
                    ?? group.OrderByDescending(e => e.Level).First())
                .OrderBy(e => e.Slot)
                .ToList();
            return result;
        }

        private static bool TryBuildEquipmentList(
            UserInfoAdditionSnapshot snapshot,
            CharacterSummary support,
            out List<EquippedEntrySnapshot> result,
            out string reason)
        {
            result = new List<EquippedEntrySnapshot>();
            reason = null;
            var bySlot = new Dictionary<byte, EquippedEntrySnapshot>();

            foreach (var entry in snapshot.EquippedEntries)
            {
                if (entry == null)
                    continue;
                if (entry.Slot < 0 || entry.Slot > LastClientEquipmentSlot)
                {
                    reason = $"equipment slot {entry.Slot} is outside client range 0..{LastClientEquipmentSlot}";
                    return false;
                }
                var core = entry.Core;
                if (core == null)
                {
                    reason = $"slot {entry.Slot} has no ItemCore";
                    return false;
                }
                if (!IsEquipmentSlotMatch(entry.Slot, core, out var slotReason))
                {
                    reason = slotReason;
                    return false;
                }
                if (!bySlot.TryAdd((byte)entry.Slot, entry))
                {
                    reason = $"duplicate equipment slot {entry.Slot}";
                    return false;
                }
            }

            var missingAvatarSlots = Enumerable.Range(0, LastDefaultAvatarSlot + 1)
                .Where(slot => !bySlot.ContainsKey((byte)slot))
                .ToList();
            if (missingAvatarSlots.Count > 0)
            {
                var defaults = StrikerDefaultAvatarDataProvider.ResolveExact(support.Job, support.GrowType);

                foreach (var slot in missingAvatarSlots)
                {
                    // 默认外观仅使用精确 job/grow；负值或无匹配时保持缺槽。
                    if (defaults == null || defaults.Count <= slot || defaults[slot] <= 0)
                        continue;
                    var defaultItem = CreateDefaultAvatarItem((byte)slot, defaults[slot]);
                    if (!IsEquipmentSlotMatch(defaultItem.Slot, defaultItem.Core, out var slotReason))
                    {
                        reason = slotReason;
                        return false;
                    }
                    bySlot[(byte)slot] = defaultItem;
                }
            }

            result = bySlot.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
            if (result.Count > byte.MaxValue)
            {
                reason = $"equipment count {result.Count} exceeds protocol byte";
                return false;
            }
            return true;
        }

        private static EquippedEntrySnapshot CreateDefaultAvatarItem(byte slot, int itemId)
        {
            return new EquippedEntrySnapshot
            {
                Slot = slot,
                Core = ItemCore.Create(ItemCore.KindAvatar, itemId),
            };
        }

        private static bool IsEquipmentSlotMatch(short slot, ItemCore core, out string reason)
        {
            reason = null;
            var itemId = core?.ItemId ?? 0;
            var rawType = ItemMetadataResolver.ResolveEquipmentType(itemId);
            if (!EquipmentTypeInfo.TryParse(rawType, out var type) || type == EquipmentType.Unknown)
            {
                reason = $"equipment PVF type missing slot={slot} item={itemId} type={rawType ?? "<null>"}";
                return false;
            }
            if ((int)type != slot)
            {
                reason = $"equipment PVF type/slot mismatch slot={slot} item={itemId} type={type}({(int)type})";
                return false;
            }
            return true;
        }

        private static bool TryLoadAndValidateState(
            int activeCharacterId,
            MercenarySupportState state,
            IGameDatabase database,
            out CharacterSummary support,
            out string reason)
        {
            support = null;
            reason = null;
            if (activeCharacterId <= 0 || activeCharacterId > ushort.MaxValue)
            {
                reason = $"owner cid is outside u16: {activeCharacterId}";
                return false;
            }
            if (state == null
                || state.OwnerCharacterId != activeCharacterId
                || state.Slot != MercenarySupportState.SingletonStateKey)
            {
                reason = "state owner/slot mismatch";
                return false;
            }
            if (state.SupportCharacterId <= 0 || state.SupportCharacterId > ushort.MaxValue
                || state.SupportCharacterId == activeCharacterId)
            {
                reason = $"invalid support cid {state.SupportCharacterId}";
                return false;
            }
            LoadCharacterPair(
                activeCharacterId,
                state.SupportCharacterId,
                database,
                out var owner,
                out support);
            if (owner == null || support == null)
            {
                reason = "owner or support character missing";
                return false;
            }
            if (owner.AccountId != support.AccountId)
            {
                reason = $"cross-account support ownerAccount={owner.AccountId} supportAccount={support.AccountId}";
                return false;
            }
            if (support.Level < StrikerSkillDataProvider.GetMinimumSupportLevel())
            {
                reason = $"support level {support.Level} is below PVF minimum {StrikerSkillDataProvider.GetMinimumSupportLevel()}";
                return false;
            }
            var skill = StrikerSkillDataProvider.FindBySkill(
                support.Job,
                support.GrowType,
                state.SkillId,
                state.StrikerSkillId);
            if (skill == null || skill.RequiredLevel > support.Level)
            {
                reason = $"skill/combo is invalid for job/grow/level: skill={state.SkillId} combo={state.StrikerSkillId}";
                return false;
            }
            if (support.Name == null || support.Name.Length == 0)
            {
                reason = "support name is empty";
                return false;
            }
            return true;
        }

        private static void LoadCharacterPair(
            int ownerCharacterId,
            int supportCharacterId,
            IGameDatabase database,
            out CharacterSummary owner,
            out CharacterSummary support)
        {
            owner = null;
            support = null;
            using (var conn = database.OpenConnection())
            using (var cmd = new SqliteCommand(@"
SELECT character_id, account_id, CAST(name AS BLOB), job, grow_type, level
FROM characters
WHERE character_id IN (@owner, @support) AND delete_flag=0", conn))
            {
                cmd.Parameters.AddWithValue("@owner", ownerCharacterId);
                cmd.Parameters.AddWithValue("@support", supportCharacterId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var current = new CharacterSummary
                        {
                            CharacterId = reader.GetInt32(0),
                            AccountId = reader.GetInt32(1),
                            Name = reader.GetValue(2) as byte[],
                            Job = (byte)reader.GetInt32(3),
                            GrowType = (byte)reader.GetInt32(4),
                            Level = (byte)reader.GetInt32(5),
                        };
                        if (current.CharacterId == ownerCharacterId)
                            owner = current;
                        if (current.CharacterId == supportCharacterId)
                            support = current;
                    }
                }
            }
        }

        internal static byte[] BuildRecordForTest(
            ushort mappedCharacterId,
            byte[] name,
            byte level,
            byte job,
            byte growType,
            ushort selectedSkillId,
            UserInfoAdditionSnapshot snapshot,
            IReadOnlyList<EquippedEntrySnapshot> equipment,
            IReadOnlyList<SkillInfoEntrySnapshot> skills)
        {
            return BuildRecord(mappedCharacterId, name, level, job, growType, selectedSkillId,
                snapshot, equipment ?? Array.Empty<EquippedEntrySnapshot>(), skills);
        }

        internal static bool IsEquipmentSlotMatchForTest(byte slot, int itemId)
        {
            return IsEquipmentSlotMatch(slot, ItemCore.Create(ItemCore.KindEquipment, itemId), out _);
        }

        internal static List<SkillInfoEntrySnapshot> NormalizeSkillPageEntriesForTest(
            MercenarySupportState state,
            IReadOnlyList<SkillInfoEntrySnapshot> learned)
        {
            return NormalizeSkillPageEntries(state, learned);
        }

        private sealed class CharacterSummary
        {
            public int CharacterId { get; set; }
            public int AccountId { get; set; }
            public byte[] Name { get; set; }
            public byte Job { get; set; }
            public byte GrowType { get; set; }
            public byte Level { get; set; }
        }
    }
}
