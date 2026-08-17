using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class RentalInfoPanelNotifier
    {
        internal const ushort NotiRental =
            (ushort)NotiPacketTypeA21.EQUIPMENT_RENTAL_LIST;

        // 幸运星或租赁物品变化后，按 A21 租赁列表 reader 刷新完整状态。
        internal static async Task SyncAsync(
            EnhancedClientSession session,
            SqliteSelectCharacterDataSource dataSource,
            int characterId,
            ushort luckyStar,
            IRentalTimeProvider rentalTimeProvider)
        {
            if (session == null || dataSource == null || characterId <= 0)
                return;

            var rental = dataSource.LoadRentalInfo(characterId);
            var now = (rentalTimeProvider ?? SystemRentalTimeProvider.Instance).UtcNowUnixSeconds();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, NotiRental,
                RentalInfoBodyBuilder.BuildWireBody(luckyStar, rental, now)));
        }
    }
}
