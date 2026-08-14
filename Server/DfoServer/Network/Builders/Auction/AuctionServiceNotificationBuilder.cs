using DfoServer.Network;

namespace DfoServer.Network.Builders.Auction
{
    public static class AuctionServiceNotificationBuilder
    {
        private const byte ServiceType0 = 0x00;
        private const byte ServiceType1 = 0x01;
        private const byte OpenState = 0x01;

        public static byte[][] BuildOpenPackets()
        {
            return new[]
            {
                BuildPacket(ServiceType0),
                BuildPacket(ServiceType1)
            };
        }

        private static byte[] BuildPacket(byte serviceType)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(serviceType);
            writer.WriteByte(OpenState);
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.AUCTION_NOTIFY_AUCTION_SERVICE,
                writer.ToArray());
        }
    }
}
