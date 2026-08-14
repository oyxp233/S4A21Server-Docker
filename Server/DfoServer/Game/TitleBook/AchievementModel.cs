using System.Collections.Generic;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.TitleBook
{
    internal sealed class AchievementModel
    {
        private readonly Dictionary<int, AchievementCompleteEntrySnapshot> _entries =
            new Dictionary<int, AchievementCompleteEntrySnapshot>();
        private readonly List<int> _order = new List<int>();
        private readonly HashSet<int> _dirtyQuestIds = new HashSet<int>();

        public IReadOnlyCollection<int> DirtyQuestIds => new List<int>(_dirtyQuestIds);

        public void LoadForCharacter(SqliteConnection connection, int characterId)
        {
            var snapshot = CharacterAchievementRepository.LoadAchievementComplete(connection, null, characterId);
            foreach (var entry in snapshot.Entries)
                Attach(entry);
        }

        public AchievementCompleteEntrySnapshot GetOrCreateEntry(int questId, ushort initialRemain1)
        {
            if (_entries.TryGetValue(questId, out var entry))
                return entry;

            entry = new AchievementCompleteEntrySnapshot
            {
                AchievementId = questId,
                P1 = initialRemain1,
                P2 = 0,
                P3 = 0,
                P4 = 0,
            };
            _entries[questId] = entry;
            _order.Add(questId);
            _dirtyQuestIds.Add(questId);
            return entry;
        }

        public void MarkDirty(int questId)
        {
            if (_entries.ContainsKey(questId))
                _dirtyQuestIds.Add(questId);
        }

        public IReadOnlyList<AchievementCompleteEntrySnapshot> GetDirtyEntries()
        {
            var result = new List<AchievementCompleteEntrySnapshot>();
            foreach (var questId in _dirtyQuestIds)
            {
                if (_entries.TryGetValue(questId, out var entry))
                    result.Add(Clone(entry));
            }

            return result;
        }

        public AchievementCompleteSnapshot BuildSnapshot()
        {
            var snapshot = new AchievementCompleteSnapshot();
            foreach (var questId in _order)
            {
                if (_entries.TryGetValue(questId, out var entry))
                    snapshot.Entries.Add(Clone(entry));
            }

            return snapshot;
        }

        public void ClearDirtyState()
        {
            _dirtyQuestIds.Clear();
        }

        private void Attach(AchievementCompleteEntrySnapshot entry)
        {
            if (entry == null)
                return;

            if (!_entries.ContainsKey(entry.AchievementId))
                _order.Add(entry.AchievementId);

            _entries[entry.AchievementId] = Clone(entry);
        }

        private static AchievementCompleteEntrySnapshot Clone(AchievementCompleteEntrySnapshot entry)
        {
            return new AchievementCompleteEntrySnapshot
            {
                AchievementId = entry.AchievementId,
                P1 = entry.P1,
                P2 = entry.P2,
                P3 = entry.P3,
                P4 = entry.P4,
            };
        }
    }
}
