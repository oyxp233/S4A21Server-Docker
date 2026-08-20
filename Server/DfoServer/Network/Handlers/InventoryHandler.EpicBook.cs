using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_EPIC_BOOK_MAKE_ITEM(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryParseEpicBookMakeItemRequest(body, out var outputEquipmentId))
            {
                await SendEpicBookMakeItemAck(session, header.type, false, EpicPieceCraftService.DefaultErrorCode);
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendEpicBookMakeItemAck(session, header.type, false, EpicPieceCraftService.DefaultErrorCode);
                return;
            }

            EpicPieceCraftResult result;
            bool canCraft;
            lock (lease.SyncRoot)
            {
                canCraft = EpicPieceCraftService.CanCraft(
                    lease.Inventory,
                    outputEquipmentId,
                    out result);
            }
            if (!canCraft)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] EPIC_BOOK_MAKE_ITEM rejected cid={characterId} " +
                    $"output=0x{outputEquipmentId:X8}");
                await SendEpicBookMakeItemAck(session, header.type, false, result?.ErrorCode ?? EpicPieceCraftService.DefaultErrorCode);
                return;
            }

            var applied = false;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "epic-book-make-item",
                (connection, transaction) =>
                {
                    var transactionSink = new TransactionBoundInventoryOverflowRewardSink(
                        connection,
                        transaction,
                        _overflowRewardSink);
                    applied = EpicPieceCraftService.TryCraft(
                        lease.Inventory,
                        outputEquipmentId,
                        transactionSink,
                        out result);
                    return applied;
                });
            if (!applied || !committed || result == null || !result.Success)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] EPIC_BOOK_MAKE_ITEM failed cid={characterId} " +
                    $"output=0x{outputEquipmentId:X8} committed={committed} applied={applied}");
                await SendEpicBookMakeItemAck(session, header.type, false, result?.ErrorCode ?? EpicPieceCraftService.DefaultErrorCode);
                return;
            }

            await SendEpicBookMakeItemAck(session, header.type, true, 0);
            await InventoryRefreshSender.SendEpicPieceInfo(
                session,
                result.EpicPieceId,
                result.EpicPieceBalance);

            var mainSlots = BuildMainRefreshSlots(result.Changes);
            if (mainSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, mainSlots);

            FileLogger.Log(
                $"[{ProtocolName}] EPIC_BOOK_MAKE_ITEM ok cid={characterId} " +
                $"output=0x{outputEquipmentId:X8} piece=0x{result.EpicPieceId:X8} " +
                $"pieceBalance={result.EpicPieceBalance} slot={result.OutputSlotIndex} " +
                $"mail={result.DeliveredByMail}");
        }

        private static bool TryParseEpicBookMakeItemRequest(
            byte[] body,
            out int outputEquipmentId)
        {
            outputEquipmentId = 0;
            if (body == null || body.Length < 4)
                return false;

            outputEquipmentId = BitConverter.ToInt32(body, 0);
            return outputEquipmentId > 0;
        }

        private static Task SendEpicBookMakeItemAck(
            EnhancedClientSession session,
            ushort packetType,
            bool success,
            byte errorCode)
        {
            var body = success
                ? new byte[] { 1 }
                : new byte[] { 0, errorCode == 0 ? EpicPieceCraftService.DefaultErrorCode : errorCode };
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, packetType, body));
        }

        private static List<short> BuildMainRefreshSlots(InventoryMutationSet changes)
        {
            var result = new List<short>();
            if (changes == null)
                return result;

            foreach (var slot in changes.Slots)
            {
                if (slot.ListType != InventoryListType.Main)
                    continue;
                if (!result.Contains(slot.SlotIndex))
                    result.Add(slot.SlotIndex);
            }

            return result;
        }
    }
}
