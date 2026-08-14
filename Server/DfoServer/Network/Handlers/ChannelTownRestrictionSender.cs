using DfoServer.Network.Builders;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class ChannelTownRestrictionSender
    {
        private const string RestrictionMessage =
            "\u5F53\u524D\u9891\u9053\u65E0\u6CD5\u524D\u5F80\u5176\u4ED6\u57CE\u9547\u3002";

        internal static async Task SendAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null)
                return;

            var current = TownAreaNotificationBuilder.CreateCurrentSnapshot(
                session.Player);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(current)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                ServerNoticeMessageBuilder.Build(RestrictionMessage)));
        }
        internal static async Task SendCurrentAreaAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null)
                return;
            var current = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(current)));
        }
    }
}
