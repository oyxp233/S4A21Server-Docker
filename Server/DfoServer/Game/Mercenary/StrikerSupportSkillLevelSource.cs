using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Mercenary
{
    // 只读取活动技能页的真实已学等级；缺失为 0，PVF 合法性由 StrikerSkillDataProvider 校验。
    internal static class StrikerSupportSkillLevelSource
    {
        public static Dictionary<ushort, byte> LoadLearnedLevels(int characterId)
        {
            var result = new Dictionary<ushort, byte>();
            foreach (var entry in LoadActiveSkillPageEntries(characterId))
            {
                if (!result.TryGetValue(entry.SkillId, out var existing) || entry.Level > existing)
                    result[entry.SkillId] = entry.Level;
            }

            return result;
        }

        public static IReadOnlyList<SkillInfoEntrySnapshot> LoadActiveSkillPageEntries(
            int characterId,
            byte? knownPageIndex = null)
        {
            return LoadActiveSkillPageEntries(
                characterId,
                GameDatabase.CreateDefault(),
                knownPageIndex);
        }

        public static IReadOnlyList<SkillInfoEntrySnapshot> LoadActiveSkillPageEntries(
            int characterId,
            IGameDatabase database,
            byte? knownPageIndex = null)
        {
            if (characterId <= 0)
                return Array.Empty<SkillInfoEntrySnapshot>();
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            try
            {
                var snapshot = new SqliteCharacterProgressRepository(database)
                    .LoadSkills(characterId);
                if (snapshot?.Pages == null || snapshot.Pages.Count == 0)
                    return Array.Empty<SkillInfoEntrySnapshot>();

                var pageIndex = knownPageIndex
                    ?? new SqliteSubtype1Repository(database)
                        .LoadSkillTreeIndex(characterId)
                    ?? 0;
                if (pageIndex >= snapshot.Pages.Count)
                    pageIndex = 0;

                return snapshot.Pages[pageIndex].Entries
                    .Where(entry => entry != null && entry.SkillId != 0 && entry.Level > 0)
                    .OrderBy(entry => entry.Slot)
                    .ToList();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[StrikerSupport] load active skill page failed cid={characterId}: {ex.Message}");
                return Array.Empty<SkillInfoEntrySnapshot>();
            }
        }

    }
}
