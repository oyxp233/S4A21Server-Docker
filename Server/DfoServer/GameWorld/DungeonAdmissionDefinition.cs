using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal enum DungeonAdmissionMode
    {
        Unknown = 0,
        Unrestricted = 1,
        QuestGated = 2,
        ActiveQuestOnly = 3,
    }

    internal readonly struct DungeonAdmissionDecision
    {
        internal DungeonAdmissionDecision(
            bool allowed,
            DungeonAdmissionMode mode,
            string reason,
            IReadOnlyList<int> requiredQuestIds)
        {
            Allowed = allowed;
            Mode = mode;
            Reason = reason ?? string.Empty;
            RequiredQuestIds = requiredQuestIds ?? Array.Empty<int>();
        }

        internal bool Allowed { get; }
        internal DungeonAdmissionMode Mode { get; }
        internal string Reason { get; }
        internal IReadOnlyList<int> RequiredQuestIds { get; }
    }

    internal sealed class DungeonAdmissionDefinition
    {
        internal DungeonAdmissionDefinition(
            int dungeonId,
            bool hasUnrestrictedEntry,
            IReadOnlyList<int> persistentQuestIds,
            IReadOnlyList<int> activeQuestIds,
            bool hasMalformedEntry)
        {
            DungeonId = dungeonId;
            HasUnrestrictedEntry = hasUnrestrictedEntry;
            PersistentQuestIds = persistentQuestIds ?? Array.Empty<int>();
            ActiveQuestIds = activeQuestIds ?? Array.Empty<int>();
            HasMalformedEntry = hasMalformedEntry;

            if (hasMalformedEntry)
                Mode = DungeonAdmissionMode.Unknown;
            else if (hasUnrestrictedEntry)
                Mode = DungeonAdmissionMode.Unrestricted;
            else if (PersistentQuestIds.Count > 0)
                Mode = DungeonAdmissionMode.QuestGated;
            else if (ActiveQuestIds.Count > 0)
                Mode = DungeonAdmissionMode.ActiveQuestOnly;
            else
                Mode = DungeonAdmissionMode.Unknown;
        }

        internal int DungeonId { get; }
        internal bool HasUnrestrictedEntry { get; }
        internal IReadOnlyList<int> PersistentQuestIds { get; }
        internal IReadOnlyList<int> ActiveQuestIds { get; }
        internal bool HasMalformedEntry { get; }
        internal DungeonAdmissionMode Mode { get; }

        internal bool IsTaskExclusive =>
            Mode == DungeonAdmissionMode.ActiveQuestOnly;

        internal DungeonAdmissionDecision Evaluate(
            ISet<int> activeQuestIds,
            ISet<int> clearedQuestIds)
        {
            if (Mode == DungeonAdmissionMode.Unknown)
            {
                return CreateDecision(
                    allowed: false,
                    reason: "malformed_or_empty_worldmap_definition");
            }

            if (HasUnrestrictedEntry)
                return CreateDecision(allowed: true, reason: "unrestricted_entry");

            if (Intersects(PersistentQuestIds, activeQuestIds))
            {
                return CreateDecision(
                    allowed: true,
                    reason: "persistent_gate_quest_active");
            }

            if (Intersects(PersistentQuestIds, clearedQuestIds))
            {
                return CreateDecision(
                    allowed: true,
                    reason: "persistent_gate_quest_cleared");
            }

            if (Intersects(ActiveQuestIds, activeQuestIds))
            {
                return CreateDecision(
                    allowed: true,
                    reason: "active_only_quest_active");
            }

            return CreateDecision(allowed: false, reason: "quest_state_miss");
        }

        private DungeonAdmissionDecision CreateDecision(
            bool allowed,
            string reason)
        {
            var required = new List<int>(
                PersistentQuestIds.Count + ActiveQuestIds.Count);
            AddDistinct(required, PersistentQuestIds);
            AddDistinct(required, ActiveQuestIds);
            return new DungeonAdmissionDecision(
                allowed,
                Mode,
                reason,
                required);
        }

        private static bool Intersects(
            IReadOnlyList<int> required,
            ISet<int> actual)
        {
            if (required == null || actual == null || actual.Count == 0)
                return false;

            foreach (var questId in required)
            {
                if (questId > 0 && actual.Contains(questId))
                    return true;
            }
            return false;
        }

        private static void AddDistinct(
            List<int> destination,
            IReadOnlyList<int> source)
        {
            if (source == null)
                return;

            foreach (var questId in source)
            {
                if (questId > 0 && !destination.Contains(questId))
                    destination.Add(questId);
            }
        }
    }
}
