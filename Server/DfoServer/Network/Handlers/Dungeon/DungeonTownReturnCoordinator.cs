using System;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonTownReturnCoordinator
    {
        private readonly DungeonInstanceRegistry _instanceRegistry;
        private readonly DungeonProgressNotificationProjector
            _progressNotifications;

        internal DungeonTownReturnCoordinator(
            DungeonInstanceRegistry instanceRegistry,
            DungeonProgressNotificationProjector progressNotifications)
        {
            _instanceRegistry = instanceRegistry
                ?? throw new ArgumentNullException(nameof(instanceRegistry));
            _progressNotifications = progressNotifications
                ?? throw new ArgumentNullException(nameof(progressNotifications));
        }

        internal async Task<bool> ReturnAsync(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            DungeonRunEndReason reason = DungeonRunEndReason.ReturnToTown)
        {
            var sourceRun = session?.Player?.CurrentRun;
            if (sourceRun == null || !sourceRun.Matches(runIdentity))
                return false;
            var returnAnchor = sourceRun.TownReturnAnchor;
            if (!await DungeonRunLifecycle.EndRunAsync(
                    session,
                    reason,
                    runIdentity,
                    _instanceRegistry))
            {
                return false;
            }
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return false;

            DungeonRunLifecycle.ApplyTownReturnAnchor(
                session.Player,
                returnAnchor,
                session.ListenerPort);
            session.Player.UserState = 0x00;
            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(
                session.Player);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00CA,
                new byte[] { 0x00 }));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await _progressNotifications.SendUserInfoSubtype0Broadcast(session);
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"ReturnToVillage: town state + subtype0 sent " +
                $"run={runIdentity.RunId}/{runIdentity.RunGeneration}");
            return true;
        }
    }
}
