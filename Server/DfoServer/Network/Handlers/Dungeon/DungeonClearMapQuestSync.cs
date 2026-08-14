using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class DungeonClearMapQuestSync
    {
        internal static async Task SyncAsync(
            EnhancedClientSession session,
            int dungeonId,
            int mapId,
            string source,
            DungeonEventEnvelope sourceEvent = null)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;
            if (sourceEvent != null
                && !run.Matches(sourceEvent.RunIdentity))
            {
                return;
            }
            if (dungeonId <= 0 && mapId <= 0)
                return;
            if (session.GameSession?.QuestManager == null)
            {
                FileLogger.Log($"[DungeonHandler] CLEAR_MAP sync skipped questManager=null: source={source} dungeon={dungeonId} map={mapId}");
                return;
            }
            if (!run.TryMarkClearMapQuestSynced(dungeonId, mapId))
            {
                FileLogger.Log($"[DungeonHandler] CLEAR_MAP sync skipped duplicate: source={source} dungeon={dungeonId} map={mapId}");
                return;
            }

            sourceEvent ??= DungeonEventEnvelope.Create(
                run,
                session.Player.CharacterId,
                source ?? "clear-map-quest");
            try
            {
                await DungeonQuestBridge.ApplyAsync(
                    session,
                    DungeonQuestProgressEvent.ClearMap(
                        sourceEvent,
                        dungeonId,
                        mapId));
            }
            catch
            {
                run.UnmarkClearMapQuestSynced(dungeonId, mapId);
                throw;
            }
        }
    }
}
