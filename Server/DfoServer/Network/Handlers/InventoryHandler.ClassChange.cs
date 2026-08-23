using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Characters;
using DfoServer.Network.Parsers.Characters;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        private static readonly ClassChangeItemApplicationService
            ClassChangeItemService =
                new ClassChangeItemApplicationService();

        public async Task Handle_USE_RIGHT_OF_CHANGE_GROW_TYPE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            ClassChangeItemRequest request = null;
            if (session?.Player == null
                || !ClassChangeItemRequestParser.TryParse(body, out request))
            {
                await SendClassChangeItemAck(
                    session,
                    header.type,
                    BuildRejectedClassChangeItem(request));
                FileLogger.Log(
                    $"[{ProtocolName}] USE_RIGHT_OF_CHANGE_GROW_TYPE rejected malformed body({body?.Length ?? 0}B)");
                return;
            }

            var characterId = session.Player.CharacterId;
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendClassChangeItemAck(
                    session,
                    header.type,
                    BuildRejectedClassChangeItem(request));
                FileLogger.Log(
                    $"[{ProtocolName}] USE_RIGHT_OF_CHANGE_GROW_TYPE rejected without owned inventory lease cid={characterId}");
                return;
            }

            ClassChangeItemService.TryUse(
                lease,
                request,
                out var result,
                out var persistenceFailed);

            if (result.Success)
            {
                session.Player.GrowType = (byte)Math.Max(
                    0,
                    Math.Min(byte.MaxValue, result.NewGrowType));

                await SendClassChangeItemRefreshes(session, lease, result);
                if (session.GameSession?.QuestManager != null)
                    await session.GameSession.QuestManager
                        .SendGrowupChangeRefreshAsync();
            }
            else
            {
                await SendClassChangeItemAck(session, header.type, result);
                await SendClassChangeItemRefreshes(session, lease, result);
            }

            if (result.Success)
                await SendClassChangeItemAck(session, header.type, result);

            FileLogger.Log(
                $"[{ProtocolName}] USE_RIGHT_OF_CHANGE_GROW_TYPE result={result.Status} " +
                $"itemSlot={request.ItemSlotIndex} target={request.TargetGrowType} " +
                $"item=0x{result.ItemTemplateId:X8} mode={result.Mode} " +
                $"grow=0x{result.PreviousGrowType:X2}->0x{result.NewGrowType:X2} " +
                $"removedQuest={result.RemovedQuestCount} " +
                $"markedAwakeningQuest={result.MarkedAwakeningQuestCount} " +
                $"persistenceFailed={persistenceFailed} detail={result.Detail}");
        }

        private static Task SendClassChangeItemAck(
            EnhancedClientSession session,
            ushort packetType,
            ClassChangeItemResult result)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                ClassChangeItemAckBuilder.Build(result)));
        }

        private async Task SendClassChangeItemRefreshes(
            EnhancedClientSession session,
            InventoryLease lease,
            ClassChangeItemResult result)
        {
            if (session == null || result == null)
                return;

            if (result.MainRefreshSlots.Count > 0)
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    result.MainRefreshSlots);

            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(
                    session,
                    result.UsableCountState);

            if (result.SourceMutation != null)
            {
                session.GameSession?.QuestManager
                    ?.RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                        lease,
                        result.SourceMutation);
            }
        }

        private static ClassChangeItemResult BuildRejectedClassChangeItem(
            ClassChangeItemRequest request)
        {
            return new ClassChangeItemResult
            {
                Request = request ?? new ClassChangeItemRequest(),
                Status = ClassChangeItemStatus.InvalidRequest,
                Detail = "request rejected before application service",
            };
        }
    }
}
