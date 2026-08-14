using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class LuckyStarClientNotifier
    {
        // 租赁商店购买后需要刷新商店目录、弹出提示、同步租赁面板。
        internal static async Task SyncPurchaseAsync(
            EnhancedClientSession session,
            SqliteSelectCharacterDataSource dataSource,
            int characterId,
            int accountId,
            ushort changeCount,
            ushort totalLuckyStar,
            IRentalTimeProvider rentalTimeProvider,
            byte[] requestBody = null)
        {
            if (session == null || dataSource == null || characterId <= 0 || accountId <= 0 || changeCount == 0)
                return;

            var accountCatalog = dataSource.LoadAccountMainOption(accountId);
            var catalogAck = RentalCatalogCodec.BuildPurchaseAck(accountCatalog, changeCount, totalLuckyStar);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00C5, catalogAck));
            await NotifyRewardAsync(session, dataSource, characterId, changeCount, totalLuckyStar, rentalTimeProvider, requestBody);
        }

        // 非商店来源只需要获得提示和租赁面板星数刷新。
        internal static async Task NotifyRewardAsync(
            EnhancedClientSession session,
            SqliteSelectCharacterDataSource dataSource,
            int characterId,
            ushort changeCount,
            ushort totalLuckyStar,
            IRentalTimeProvider rentalTimeProvider,
            byte[] requestBody = null)
        {
            if (session == null || dataSource == null || characterId <= 0 || changeCount == 0)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0373,
                Build0373SuccessAck(changeCount, totalLuckyStar, requestBody)));
            await RentalInfoPanelNotifier.SyncAsync(session, dataSource, characterId, totalLuckyStar, rentalTimeProvider);
        }

        private static byte[] Build0373SuccessAck(ushort changeCount, ushort totalLuckyStar, byte[] requestBody)
        {
            var requestLength = Math.Max(requestBody?.Length ?? 0, RentalCatalogCodec.ShopPacketQtyOffset + 4);
            var body = new byte[1 + requestLength];
            body[0] = 0x01;

            if (requestBody != null && requestBody.Length > 0)
                Buffer.BlockCopy(requestBody, 0, body, 1, requestBody.Length);

            Buffer.BlockCopy(BitConverter.GetBytes((int)totalLuckyStar), 0, body, 1 + 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)changeCount), 0, body, 1 + RentalCatalogCodec.ShopPacketQtyOffset, 4);
            return body;
        }

    }
}
