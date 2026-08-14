using System;
using System.Threading.Tasks;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;

namespace DfoServer.Network.Handlers
{
    internal sealed class ExpertJobGiveupHandler
    {
        private const ushort GiveupCommand = (ushort)CmdPacketType.GIVEUP_EXPERT_JOB;

        private readonly ExpertJobStoreRuntimeService _stores;
        private readonly ExpertJobGiveupApplicationService _giveup;
        private readonly ExpertJobGiveupNotificationProjector _notifications;
        private readonly ExpertJobOperationCoordinator _operations;

        internal ExpertJobGiveupHandler(
            ExpertJobStoreRuntimeService stores,
            ExpertJobGiveupApplicationService giveup,
            ExpertJobGiveupNotificationProjector notifications,
            ExpertJobOperationCoordinator operations)
        {
            _stores = stores ?? throw new ArgumentNullException(nameof(stores));
            _giveup = giveup ?? throw new ArgumentNullException(nameof(giveup));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        internal async Task Handle(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session?.Player;
            var expertJobType = player?.Subtype0Tail?.ExpertJobType ?? 0;
            if (!GiveupExpertJobRequest.IsValid(body)
                || player == null
                || player.CharacterId <= 0
                || player.CurrentRun != null
                || !ExpertJobGiveupConfigProvider.TryGet(expertJobType, out var config)
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendError(session, ExpertJobGiveupResult.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            var committed = false;
            try
            {
                if (!ReferenceEquals(session.Player, player)
                    || player.CurrentRun != null
                    || player.Subtype0Tail?.ExpertJobType != config.ExpertJobType
                    || !InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        player.CharacterId)
                    || _stores.HasStore(player.CharacterId)
                    || _stores.TryGetEnteredStore(
                        session.SessionId,
                        player.CharacterId,
                        out _))
                {
                    await SendError(session, ExpertJobGiveupResult.ErrorInvalidState);
                    return;
                }

                var result = _giveup.Apply(lease, session.SessionId, config);
                if (!result.Success)
                {
                    await SendError(session, result.ErrorCode);
                    return;
                }
                committed = true;

                await _notifications.ProjectAsync(session, result);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    GiveupCommand,
                    ExpertJobGiveupPacketBuilder.BuildSuccess(result)));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobGiveup] handler failed cid={player.CharacterId}: {ex.Message}");
                if (!committed)
                    await SendError(session, ExpertJobGiveupResult.ErrorPersistence);
            }
            finally
            {
                operationGate.Release();
            }
        }

        private static Task SendError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                GiveupCommand,
                ExpertJobGiveupPacketBuilder.BuildError(errorCode)));
    }
}
