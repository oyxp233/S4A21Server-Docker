using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Cross-run dungeon mechanisms use an instance coordinator because their
    // state belongs to repositories, not to DungeonRun. Anton normal conquest
    // is the first such mechanism; ordinary one-run mechanisms remain static.
    internal sealed class DungeonPersistentMechanismCoordinator
    {
        private readonly AntonNormalConquestNotifier _antonNormal;
        private readonly AntonAwakeningDailyProgressService _awakeningProgress;

        internal DungeonPersistentMechanismCoordinator(
            SqliteCharacterStateRepository characterStateRepository,
            AntonAwakeningDailyProgressService awakeningProgress = null)
        {
            _antonNormal = new AntonNormalConquestNotifier(
                characterStateRepository,
                awakeningProgress);
            _awakeningProgress = awakeningProgress;
        }

        internal Task RestoreBeforeSelectionAsync(
            EnhancedClientSession session)
            => _antonNormal.RestoreBeforeSelectAsync(session);

        internal void ConfigureLinkedChallenge(DungeonRun run)
            => _antonNormal.ConfigureLinkedChallenge(run);

        internal byte ResolveSequentialProgress(int characterId, int configKey)
            => _antonNormal.ResolveSequentialProgress(characterId, configKey);

        internal bool TryResolveSequentialState(
            int characterId,
            int configKey,
            out AntonNormalSyncState state)
            => _antonNormal.TryResolveSequentialState(
                characterId,
                configKey,
                out state);

        internal AntonAwakeningAdmissionDecision EvaluateEntryAdmission(
            int characterId,
            int dungeonId)
        {
            if (_awakeningProgress == null)
            {
                throw new InvalidOperationException(
                    "Sequential daily progress service is unavailable.");
            }
            return _awakeningProgress.EvaluateAdmission(
                characterId,
                dungeonId);
        }

        internal Task ApplyDungeonClearAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => _antonNormal.ApplyClearAsync(session, run);
    }
}
