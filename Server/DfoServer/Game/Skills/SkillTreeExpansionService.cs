using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Skills
{
    /// <summary>
    /// 角色级第二技能页永久解锁。调用方必须将本服务放在商城扣款所在事务中。
    /// </summary>
    public static class SkillTreeExpansionService
    {
        public static bool TryUnlock(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            var subtypeRepository = SqliteSubtype1Repository.FromConnectionString(connection.ConnectionString);
            var stored = subtypeRepository.LoadSkillTreeIndex(connection, transaction, characterId)
                ?? SkillTreeExpansionState.LockedWireValue;
            if (SkillTreeExpansionState.IsUnlocked(stored))
                return false;

            var progressRepository = SqliteCharacterProgressRepository.FromConnectionString(connection.ConnectionString);
            var owner = progressRepository.LoadProgressSnapshot(connection, transaction, characterId);
            if (owner == null)
                return false;

            Characters.CharacterStatComputer.DecodeGrowType(owner.GrowType, out var firstGrow, out var secondGrow);
            var current = progressRepository.LoadSkills(connection, transaction, characterId);
            var initial = CharacterSkillProfile.BuildSnapshot(owner.Job, firstGrow, secondGrow, owner.Level);
            if (current == null || initial == null || initial.Pages.Count < 2)
                return false;

            while (current.Pages.Count < 2)
                current.Pages.Add(new SkillInfoPageSnapshot());

            // 仅初始化第二页；第一页及其已学习技能保持不变。
            current.Pages[1] = ClonePage(initial.Pages[1]);
            progressRepository.SaveSkillProgress(connection, transaction, characterId, current);

            return subtypeRepository.UpdateSkillTreeIndex(
                connection, transaction, characterId, 0) > 0;
        }

        private static SkillInfoPageSnapshot ClonePage(SkillInfoPageSnapshot source)
        {
            var page = new SkillInfoPageSnapshot { HeaderValue = source?.HeaderValue ?? 0 };
            if (source == null)
                return page;

            foreach (var entry in source.Entries)
            {
                var clone = new SkillInfoEntrySnapshot
                {
                    Slot = entry.Slot,
                    SkillId = entry.SkillId,
                    Level = entry.Level,
                };
                clone.ExtraValues.AddRange(entry.ExtraValues);
                page.Entries.Add(clone);
            }
            return page;
        }
    }
}
