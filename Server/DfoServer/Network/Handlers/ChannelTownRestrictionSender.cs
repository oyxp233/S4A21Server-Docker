using DfoServer.Network.Builders;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class ChannelTownRestrictionSender
    {
        private const string RestrictionMessage =
            "当前频道无法前往其他城镇。";
        private const string Channel100TownRestrictionMessage =
            "当前频道无法前往圣者之鸣号。";

        internal static string ResolveRestrictionMessage(
            int listenerGamePort,
            int? targetTownId)
            => targetTownId == GameChannelSpawnPolicy.Channel100TownId
               && !GameNetworkConfig.IsChannel100Listener(listenerGamePort)
               && !GameNetworkConfig.IsRaidListener(listenerGamePort)
                ? Channel100TownRestrictionMessage
                : RestrictionMessage;

        internal static async Task SendAsync(
            EnhancedClientSession session,
            int? targetTownId = null)
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
                ServerNoticeMessageBuilder.Build(
                    ResolveRestrictionMessage(
                        session.ListenerPort,
                        targetTownId))));
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
