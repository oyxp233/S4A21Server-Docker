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
            ushort changeCount,
            ushort totalLuckyStar,
            IRentalTimeProvider rentalTimeProvider,
            byte[] requestBody = null)
        {
            if (session == null || dataSource == null || characterId <= 0 || changeCount == 0)
                return;

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

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.CHARGE_RENTPOINT,
                BuildChargeRentPointSuccessBody(changeCount, totalLuckyStar, requestBody)));
            await RentalInfoPanelNotifier.SyncAsync(session, dataSource, characterId, totalLuckyStar, rentalTimeProvider);
        }

        internal static byte[] BuildChargeRentPointSuccessBody(
            ushort changeCount,
            ushort totalLuckyStar,
            byte[] requestBody)
        {
            var mode = 2;
            var quantity = (int)changeCount;
            if (requestBody != null
                && requestBody.Length >= RentalCatalogCodec.ChargeRentPointRequestSize)
            {
                mode = BitConverter.ToInt32(
                    requestBody,
                    RentalCatalogCodec.ChargeRentPointModeOffset);
                quantity = BitConverter.ToInt32(
                    requestBody,
                    RentalCatalogCodec.ChargeRentPointQuantityOffset);
            }

            var body = new byte[13];
            body[0] = 0x01;
            Buffer.BlockCopy(BitConverter.GetBytes(mode), 0, body, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(quantity), 0, body, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)totalLuckyStar), 0, body, 9, 4);
            return body;
        }

    }
}
