using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal enum QuestProgressOperation
    {
        ClientTrigger = 0,
        HuntMonster = 1,
        ClearMap = 2,
        ClearDungeon = 3,
        SeekingItems = 4,
        HuntEnemy = 5,
    }

    internal sealed class QuestProgressApplicationRequest
    {
        internal int CharacterId { get; set; }
        internal QuestProgressOperation Operation { get; set; }
        internal Guid SourceEventId { get; set; }
        internal ushort QuestId { get; set; }
        internal byte TriggerType { get; set; }
        internal bool Increment { get; set; }
        internal int DungeonId { get; set; }
        internal int Difficulty { get; set; }
        internal int MapId { get; set; }
        internal int MonsterCode { get; set; }
        internal byte MonsterType { get; set; }
        internal int EnemyType { get; set; }
        internal IReadOnlyCollection<ushort> EligibleQuestIds { get; set; }
        internal IReadOnlyDictionary<ushort, QuestActivationId>
            EligibleQuestActivations { get; set; }
        internal IReadOnlyCollection<int> ItemFilter { get; set; }
        internal IReadOnlyDictionary<int, int> HeldItemCounts { get; set; }
        internal QuestCommandOwnerContext? CommandOwner { get; set; }

        internal string EventKind
        {
            get
            {
                switch (Operation)
                {
                    case QuestProgressOperation.HuntMonster:
                        return "hunt-monster";
                    case QuestProgressOperation.HuntEnemy:
                        return "hunt-enemy";
                    case QuestProgressOperation.ClearDungeon:
                        return "clear-dungeon";
                    case QuestProgressOperation.ClearMap:
                        return "clear-map";
                    case QuestProgressOperation.SeekingItems:
                        return "seeking-items";
                    default:
                        return "client-trigger";
                }
            }
        }
    }

    internal sealed class QuestProgressApplicationResult
    {
        private readonly List<QuestSetTriggerResult> _changes =
            new List<QuestSetTriggerResult>();

        internal bool Success { get; set; }
        internal bool DuplicateEvent { get; set; }
        internal bool QuestNotActive { get; set; }
        internal bool ActivationChanged { get; set; }
        internal bool MatchedObjective { get; set; }
        internal bool RetryRequired { get; set; }
        internal string Error { get; set; } = string.Empty;
        internal IReadOnlyList<QuestSetTriggerResult> Changes => _changes;

        internal void AddChanges(IEnumerable<QuestSetTriggerResult> changes)
        {
            if (changes == null)
                return;
            _changes.AddRange(changes);
        }
    }

    internal sealed class QuestProgressEvaluation
    {
        private readonly List<QuestSetTriggerResult> _changes =
            new List<QuestSetTriggerResult>();

        internal bool Matched { get; set; }
        internal QuestTrigger Trigger { get; set; }
        internal IReadOnlyList<QuestSetTriggerResult> Changes => _changes;

        internal void AddChange(
            ushort questId,
            QuestTrigger previous,
            QuestTrigger current)
        {
            _changes.Add(new QuestSetTriggerResult
            {
                QuestId = questId,
                PreviousTriggerValue = previous.PackedValue,
                TriggerValue = current.PackedValue,
            });
        }
    }
}
