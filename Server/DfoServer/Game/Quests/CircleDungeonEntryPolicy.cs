namespace DfoServer.Game.Quests
{
    internal enum CircleDungeonEntryRejectReason
    {
        None = 0,
        InvalidIdentifier,
        QuestNotFound,
        NotCircleQuest,
        DungeonMismatch,
    }

    internal readonly struct CircleDungeonEntryDecision
    {
        private CircleDungeonEntryDecision(
            ushort circleQuestId,
            CircleDungeonEntryRejectReason rejectReason)
        {
            CircleQuestId = circleQuestId;
            RejectReason = rejectReason;
        }

        internal bool Allowed => RejectReason == CircleDungeonEntryRejectReason.None;
        internal ushort CircleQuestId { get; }
        internal CircleDungeonEntryRejectReason RejectReason { get; }

        internal static CircleDungeonEntryDecision Allow(ushort circleQuestId)
            => new CircleDungeonEntryDecision(
                circleQuestId,
                CircleDungeonEntryRejectReason.None);

        internal static CircleDungeonEntryDecision Reject(
            CircleDungeonEntryRejectReason reason)
            => new CircleDungeonEntryDecision(0, reason);
    }

    internal static class CircleDungeonEntryPolicy
    {
        // This handshake validates only the immutable PVF pair. The subsequent
        // ACCEPT_QUEST command still owns active/cleared/prerequisite/slot checks.
        internal static CircleDungeonEntryDecision Evaluate(
            uint dungeonId,
            uint circleQuestId)
        {
            if (dungeonId == 0
                || dungeonId > int.MaxValue
                || circleQuestId == 0
                || circleQuestId > ushort.MaxValue)
            {
                return CircleDungeonEntryDecision.Reject(
                    CircleDungeonEntryRejectReason.InvalidIdentifier);
            }

            var quest = GameWorld.QuestData.GetQuestFile((int)circleQuestId);
            if (quest == null)
            {
                return CircleDungeonEntryDecision.Reject(
                    CircleDungeonEntryRejectReason.QuestNotFound);
            }

            if (GameWorld.QuestData.NormalizeQuestTag(quest.Grade) != "circle")
            {
                return CircleDungeonEntryDecision.Reject(
                    CircleDungeonEntryRejectReason.NotCircleQuest);
            }

            if (!GameWorld.QuestData.ReferencesDungeon(
                    (int)circleQuestId,
                    (int)dungeonId))
            {
                return CircleDungeonEntryDecision.Reject(
                    CircleDungeonEntryRejectReason.DungeonMismatch);
            }

            return CircleDungeonEntryDecision.Allow((ushort)circleQuestId);
        }
    }
}
