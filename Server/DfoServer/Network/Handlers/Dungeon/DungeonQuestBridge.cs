using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Projects typed dungeon facts into one participant's frozen quest set.
    // Quest evaluation and persistence remain owned by QuestService.
    internal static class DungeonQuestBridge
    {
        internal static Task ApplyAsync(
            EnhancedClientSession session,
            DungeonQuestProgressEvent progressEvent)
        {
            var run = session?.Player?.CurrentRun;
            var questManager = session?.GameSession?.QuestManager;
            if (run == null || questManager == null || progressEvent == null)
                return Task.CompletedTask;
            if (!run.RewardPolicy.AllowsQuestProgress)
                return Task.CompletedTask;

            var envelope = progressEvent.Envelope;
            if (!run.Matches(envelope.RunIdentity)
                || (envelope.RoomInstanceId.HasValue
                    && envelope.RoomInstanceId.Value != run.CurrentRoomInstanceId)
                || (envelope.AffectedPlayerId.HasValue
                    && envelope.AffectedPlayerId.Value != session.Player.CharacterId))
            {
                FileLogger.Log(
                    $"[DungeonQuestBridge] stale/mismatched event ignored: " +
                    $"event={envelope.SourceEventId:N} kind={progressEvent.Kind} " +
                    $"cid={session.Player.CharacterId} run={run.RunId}");
                return Task.CompletedTask;
            }

            var eligibleQuestIds = run.QuestSnapshot?.QuestIds
                ?? QuestRunSnapshot.Empty.QuestIds;
            var eligibleQuestActivations =
                run.QuestSnapshot?.Activations?.Count > 0
                    ? run.QuestSnapshot.Activations
                    : null;
            switch (progressEvent.Kind)
            {
                case DungeonQuestProgressKind.HuntMonster:
                    return questManager.SyncHuntMonsterQuestProgressAsync(
                        progressEvent.DungeonId,
                        progressEvent.Difficulty,
                        progressEvent.ActorCode,
                        progressEvent.SourceEventId,
                        eligibleQuestIds,
                        envelope.RunIdentity,
                        eligibleQuestActivations,
                        progressEvent.MonsterType);

                case DungeonQuestProgressKind.HuntEnemy:
                    return questManager.SyncHuntEnemyQuestProgressAsync(
                        progressEvent.DungeonId,
                        progressEvent.Difficulty,
                        progressEvent.ActorCode,
                        progressEvent.EnemyType,
                        progressEvent.SourceEventId,
                        eligibleQuestIds,
                        envelope.RunIdentity,
                        eligibleQuestActivations);

                case DungeonQuestProgressKind.ClearMap:
                case DungeonQuestProgressKind.ClearDungeon:
                    return questManager.SyncClearMapQuestProgressAsync(
                        progressEvent.DungeonId,
                        progressEvent.MapId,
                        progressEvent.SourceEventId,
                        eligibleQuestIds,
                        eligibleQuestActivations);

                default:
                    return Task.CompletedTask;
            }
        }
    }
}
