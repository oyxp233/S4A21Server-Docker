using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders.Characters;
using DfoServer.Network.Parsers.Characters;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class GrowupChangeHandler
    {
        private readonly GrowupChangeApplicationService _service;
        private readonly InventoryRefreshSender _inventoryRefresh;

        public GrowupChangeHandler(
            GrowupChangeApplicationService service,
            InventoryRefreshSender inventoryRefresh)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _inventoryRefresh = inventoryRefresh
                ?? throw new ArgumentNullException(nameof(inventoryRefresh));
        }

        public async Task Handle(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            GrowupChangeRequest request = null;
            if (session?.Player == null
                || !GrowupChangeRequestParser.TryParse(body, out request))
            {
                await SendAck(session, header.type, BuildRejected(session, request));
                FileLogger.Log(
                    $"[GameProtocol] RE_GROWUP_CHANGE rejected malformed body({body?.Length ?? 0}B)");
                return;
            }

            var characterId = session.Player.CharacterId;
            if (!InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    characterId,
                    out var lease))
            {
                await SendAck(session, header.type, BuildRejected(session, request));
                FileLogger.Log(
                    $"[GameProtocol] RE_GROWUP_CHANGE rejected without owned inventory lease cid={characterId}");
                return;
            }

            _service.TryChange(
                lease,
                request,
                out var result,
                out var persistenceFailed);

            if (result.Success)
            {
                session.Player.GrowType = result.NewGrowType;
                session.Player.GrowupChangeCount = result.NewChangeCount;

                if (result.GoldChanged)
                    await _inventoryRefresh.SendGoldUpdate(session, result.UpdatedGold);

                if (session.GameSession?.QuestManager != null)
                    await session.GameSession.QuestManager.SendGrowupChangeRefreshAsync();
            }
            else if (result.NewChangeCount == 0 && session.Player.GrowupChangeCount > 0)
            {
                result.NewChangeCount = session.Player.GrowupChangeCount;
            }

            await SendAck(session, header.type, result);
            FileLogger.Log(
                $"[GameProtocol] RE_GROWUP_CHANGE result={result.Status} " +
                $"code={result.ResultCode} target={request.TargetGrowType} " +
                $"count={result.PreviousChangeCount}->{result.NewChangeCount} " +
                $"goldCost={result.GoldCost} goldAfter={result.UpdatedGold} " +
                $"removedQuest={result.RemovedQuestCount} " +
                $"persistenceFailed={persistenceFailed} detail={result.Detail}");
        }

        private static Task SendAck(
            EnhancedClientSession session,
            ushort packetType,
            GrowupChangeResult result)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                GrowupChangeAckBuilder.Build(result)));
        }

        private static GrowupChangeResult BuildRejected(
            EnhancedClientSession session,
            GrowupChangeRequest request)
        {
            return new GrowupChangeResult
            {
                Status = GrowupChangeStatus.InvalidRequest,
                Detail = "request rejected before application service",
                ResultCode = GrowupChangeResult.ResultCodeInvalidState,
                TargetGrowType = request?.TargetGrowType ?? (byte)0,
                NewChangeCount = session?.Player?.GrowupChangeCount ?? 0,
            };
        }
    }
}
