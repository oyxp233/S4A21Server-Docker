using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class CollectionBoxHandler
    {
        private readonly InventoryRefreshSender _refresh;

        public CollectionBoxHandler(InventoryRefreshSender refresh)
        {
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public async Task HandleQueryCollectionBox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int boxIndex = body != null && body.Length > 0 ? body[body.Length - 1] : 0;
            var entry = CollectBoxDataService.GetByIndex(boxIndex);

            session.SelectedCollectionBoxIndex = entry != null ? boxIndex : 0;

            var w = new GamePacketWriter();
            w.WriteByte((byte)session.SelectedCollectionBoxIndex);
            w.WriteByte((byte)session.SelectedCollectionBoxIndex);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, w.ToArray()));
        }

        public async Task HandleInsertCollectBoxItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var resultCode = (byte)0;
            var errorCode = (byte)0x12;
            CollectBoxMutationResult result = null;

            if (body != null && body.Length >= 8 && TryGetOwnedLease(session, out var lease))
            {
                int boxIndex = BitConverter.ToUInt16(body, 0);
                int sourceSlotIndex = BitConverter.ToUInt16(body, 2);
                int itemId = (int)BitConverter.ToUInt32(body, 4);

                lock (lease.SyncRoot)
                {
                    if (CollectBoxRuntimeService.TryPutItem(
                            lease.Inventory,
                            boxIndex,
                            sourceSlotIndex,
                            itemId,
                            out result))
                    {
                        resultCode = 1;
                    }
                }

                if (resultCode != 0)
                {
                    if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                            lease,
                            "collectbox-insert"))
                    {
                        resultCode = 0;
                        errorCode = 0x12;
                    }
                }

                if (resultCode != 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        0x0012,
                        DeleteItemAckBuilder.Build(result.InventoryItem)));

                    await SendCollectBoxNoti(session, lease.Inventory.CollectBox, boxIndex);
                }
                else if (result != null)
                {
                    errorCode = result.ErrorCode;
                }
            }

            await SendCommandAck(session, header.type, resultCode, errorCode);
        }

        public async Task HandleRemoveCollectBoxItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var resultCode = (byte)0;
            var errorCode = (byte)0x12;
            CollectBoxMutationResult result = null;

            if (body != null && body.Length >= 5 && TryGetOwnedLease(session, out var lease))
            {
                int itemId = (int)BitConverter.ToUInt32(body, 1);

                lock (lease.SyncRoot)
                {
                    if (CollectBoxRuntimeService.TryTakeItem(
                            lease.Inventory,
                            itemId,
                            out result))
                    {
                        resultCode = 1;
                    }
                }

                if (resultCode != 0)
                {
                    if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                            lease,
                            "collectbox-remove"))
                    {
                        resultCode = 0;
                        errorCode = 0x12;
                    }
                }

                if (resultCode != 0)
                {
                    await _refresh.SendUpdateItemList(
                        session,
                        result.InventoryItem.ListType,
                        result.InventoryItem.SlotIndex);

                    await SendCollectBoxNoti(session, lease.Inventory.CollectBox, result.BoxIndex);
                }
                else if (result != null)
                {
                    errorCode = result.ErrorCode;
                }
            }

            await SendCommandAck(session, header.type, resultCode, errorCode);
        }

        private static bool TryGetOwnedLease(EnhancedClientSession session, out InventoryLease lease)
        {
            lease = null;
            var (characterId, _) = SessionOwnerResolver.Resolve(session);
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private static async Task SendCollectBoxNoti(
            EnhancedClientSession session,
            CollectBoxModel model,
            int boxIndex)
        {
            if (CollectionBoxBodyBuilder.TryBuildForBox(model, boxIndex, out var body))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x03EB, body));
        }

        private static Task SendCommandAck(
            EnhancedClientSession session,
            ushort commandType,
            byte resultCode,
            byte errorCode)
        {
            var w = new GamePacketWriter();
            w.WriteByte(resultCode);
            if (resultCode == 0)
                w.WriteByte(errorCode);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, w.ToArray()));
        }
    }
}
