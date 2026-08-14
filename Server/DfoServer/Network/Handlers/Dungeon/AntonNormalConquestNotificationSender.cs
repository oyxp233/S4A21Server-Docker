using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class AntonNormalConquestNotificationSender
    {
        private const int SequentialRouteMask = 0;

        internal async Task SendAsync(
            EnhancedClientSession session,
            AntonNormalSyncState state,
            string source,
            DungeonRunIdentity? expectedRun,
            long? expectedTownGeneration)
        {
            if (!IsExpectedProjectionContextCurrent(
                    session,
                    expectedRun,
                    expectedTownGeneration))
            {
                return;
            }

            if (state.PermissionEntries.Count > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.DUNGEON_PERMISSION,
                    DungeonPermissionBodyBuilder.BuildEntries(
                        state.PermissionEntries)));
                if (!IsExpectedProjectionContextCurrent(
                        session,
                        expectedRun,
                        expectedTownGeneration))
                {
                    return;
                }
            }

            var sequentialBody = DungeonNotificationBuilder
                .BuildSequentialDungeonInfo(
                    state.Sequence.ConfigKey,
                    state.ProgressIndex,
                    SequentialRouteMask);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.SEQUENTIAL_DUNGEON_INFO,
                sequentialBody));
            if (!IsExpectedProjectionContextCurrent(
                    session,
                    expectedRun,
                    expectedTownGeneration))
            {
                return;
            }

            FileLogger.Log(
                $"[AntonNormal] state sent: source={source} " +
                $"key={state.Sequence.ConfigKey} progress={state.ProgressIndex} " +
                $"routeMask={SequentialRouteMask} " +
                $"sequence={string.Join(",", state.Sequence.DungeonIds)} " +
                $"permissions={string.Join(",", state.PermissionEntries.Select(
                    entry => $"{entry.DungeonId}:{entry.ClearState}"))} " +
                $"body={BitConverter.ToString(sequentialBody)}");
        }

        private static bool IsExpectedProjectionContextCurrent(
            EnhancedClientSession session,
            DungeonRunIdentity? expectedRun,
            long? expectedTownGeneration)
        {
            var player = session?.Player;
            if (player == null)
                return false;
            if (expectedRun.HasValue)
                return player.IsCurrentDungeonRun(expectedRun.Value);
            if (expectedTownGeneration.HasValue)
            {
                return player.CurrentRun == null
                    && player.CurrentDungeonRunGeneration
                    == expectedTownGeneration.Value;
            }
            return true;
        }
    }
}
