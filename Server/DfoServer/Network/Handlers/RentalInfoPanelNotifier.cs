using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class RentalInfoPanelNotifier
    {
        internal const ushort NotiRental = 0x0357;

        // 0x0357 是租赁面板状态包，幸运星或租赁物品变化后主动刷新。
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
