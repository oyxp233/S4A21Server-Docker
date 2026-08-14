using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;

namespace DfoServer.Network.Handlers
{
    // CMD 0x00CF SHOP_COIN_EVENT: 每日免费复活币领取 — 纯协议壳。
    // 业务规则与事务全部在 Game/ReviveCoin/ReviveCoinService(每日功能打样)。
    public sealed class ShopCoinEventHandler
    {
        private readonly ReviveCoinService _reviveCoin;
        private readonly InventoryRefreshSender _refresh;

        public ShopCoinEventHandler(ReviveCoinService reviveCoin, InventoryRefreshSender refresh)
        {
            _reviveCoin = reviveCoin;
            _refresh = refresh;
        }

        public async Task HandleShopCoinEvent(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, _) = InventoryHandler.ResolveOwner(session);

            short assignedSlot;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId)
                || !_reviveCoin.TryGrantDaily(lease, out assignedSlot))
            {
                await SendAck(session, ok: false, grantCount: 0);
                return;
            }

            // 与初始化流 0x00B1(shop_coin_event_flag) 对应: 领取后置 1
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00B1, new byte[] { 1 }));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, assignedSlot);
            await SendAck(session, ok: true, grantCount: 1);
            FileLogger.Log($"[GameProtocol] SHOP_COIN_EVENT: granted cid={characterId} slot={assignedSlot}");
        }

        // ACK 0x00CF: u8 ok + i32 grantCount (小端, PR#338 引 149.diff)
        private static Task SendAck(EnhancedClientSession session, bool ok, int grantCount)
        {
            var w = new GamePacketWriter();
            w.WriteByte(ok ? (byte)1 : (byte)0);
            w.WriteInt32(grantCount);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CF, w.ToArray()));
        }
    }
}
