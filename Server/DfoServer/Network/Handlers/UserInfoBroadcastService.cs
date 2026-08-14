using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // NOTI 0x0002 (USERINFO) 广播的统一实现。此前 QuestManager、DungeonSharedServices、
    // ExperienceItemNotificationService 各有一份几乎相同的组包代码, 复制导致的漂移
    // 是这类缺陷(如组队漏成长胶囊)的温床, 统一后新入口只调这里。
    internal static class UserInfoBroadcastService
    {
        // subtype0(角色状态): DB 快照为权威, 荣誉应用后同步回会话缓存再发送。
        // ⚠ 副本内禁发 subtype0 -- 会打乱客户端副本内角色状态, 由调用方把关。
        internal static Task<bool> SendSubtype0Async(
            EnhancedClientSession session,
            ICharacterRepository characterRepository,
            SqliteSubtype0FieldsRepository subtype0Repository,
            HonorLevelSyncService honorLevel,
            string logTag,
            HonorLevelSummary honorSummary = null)
            => SendSubtype0Async(
                session?.Player,
                session?.Account?.AccountId ?? 0,
                body => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, body)),
                characterRepository,
                subtype0Repository,
                honorLevel,
                logTag,
                honorSummary);

        internal static async Task<bool> SendSubtype0Async(
            PlayerContext player,
            int accountIdHint,
            Func<byte[], Task> sendUserInfoNoti,
            ICharacterRepository characterRepository,
            SqliteSubtype0FieldsRepository subtype0Repository,
            HonorLevelSyncService honorLevel,
            string logTag,
            HonorLevelSummary honorSummary = null)
        {
            try
            {
                if (player == null
                    || sendUserInfoNoti == null
                    || characterRepository == null
                    || subtype0Repository == null
                    || honorLevel == null)
                    return false;

                int cid = player.CharacterId;
                var record = characterRepository.GetById(cid);
                if (record == null)
                    return false;

                record.Subtype0Tail = subtype0Repository.Load(cid) ?? new UserInfoMinimumTailSnapshot();
                var accountId = accountIdHint > 0 ? accountIdHint : record.AccountId;
                var accountCharacters = honorSummary == null
                    ? characterRepository.ListByAccount(accountId)
                    : null;
                honorLevel.ApplyToSubtype0Tail(
                    record.Subtype0Tail,
                    accountId,
                    accountCharacters,
                    honorSummary);

                // subtype0 既是客户端通知, 也是服务端会话缓存;
                // 两端必须观察到同一份 DB 快照。
                player.Subtype0Tail = record.Subtype0Tail;

                await sendUserInfoNoti(UserInfoSubtype0Builder.BuildNotificationBody(record));
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] {logTag} ERROR: {ex.Message}");
                return false;
            }
        }

        // subtype1(属性/技能)通知体: 冒险团 + 荣誉 + 技能快照统一组包。
        // 数据获取(record/addition/技能来源)因入口而异, 由调用方提供。
        internal static byte[] BuildSubtype1Body(
            CharacterRecord record,
            UserInfoAdditionSnapshot addition,
            IEnumerable<CharacterRecord> accountCharacters,
            HonorLevelSummary honor,
            SkillInfoSnapshot skills)
        {
            AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, accountCharacters);
            HonorLevelDataProvider.ApplyToUserInfoAddition(addition, honor);
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteUInt16(1);
            w.WriteUInt16((ushort)record.CharacterId);
            w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(addition, skills));
            return w.ToArray();
        }
    }
}
