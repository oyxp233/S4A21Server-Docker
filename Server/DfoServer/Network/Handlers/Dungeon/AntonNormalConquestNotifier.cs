using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Compatibility facade: validates the current session/run context, then
    // delegates state transitions and wire projection to their owners.
    internal sealed class AntonNormalConquestNotifier
    {
        private readonly AntonNormalConquestApplicationService _application;
        private readonly AntonNormalConquestNotificationSender _sender;

        internal AntonNormalConquestNotifier(
            SqliteCharacterStateRepository repository)
        {
            _application = new AntonNormalConquestApplicationService(repository);
            _sender = new AntonNormalConquestNotificationSender();
        }

        internal void ConfigureLinkedChallenge(DungeonRun run)
            => _application.ConfigureLinkedChallenge(run);

        internal async Task RestoreBeforeSelectAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;
            var expectedTownGeneration =
                session.Player.CurrentDungeonRunGeneration;
            if (session.Player.CurrentRun != null)
                return;

            try
            {
                if (!_application.TryRestore(
                        session.Player.CharacterId,
                        out var state))
                {
                    return;
                }
                await _sender.SendAsync(
                    session,
                    state,
                    "enter-select-dungeon",
                    expectedRun: null,
                    expectedTownGeneration);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] restore skipped: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }

        internal async Task ApplyClearAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || run == null)
            {
                return;
            }

            try
            {
                if (!_application.TryApplyClear(
                        session.Player.CharacterId,
                        run.DungeonId,
                        out var result))
                {
                    return;
                }
                await _sender.SendAsync(
                    session,
                    result.State,
                    "dungeon-clear",
                    run.CaptureIdentity(),
                    expectedTownGeneration: null);
                FileLogger.Log(
                    $"[AntonNormal] clear applied: dungeon={run.DungeonId} " +
                    $"changes={(result.Changes.Count == 0
                        ? "none"
                        : string.Join(",", result.Changes.Select(
                            entry => $"{entry.DungeonId}:{entry.ClearState}")))} " +
                    $"progress={result.State.ProgressIndex}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] clear sync failed and remains retryable: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"error={ex.Message}");
                throw;
            }
        }
    }
}
