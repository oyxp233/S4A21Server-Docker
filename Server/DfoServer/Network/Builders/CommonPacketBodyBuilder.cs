using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class CommonPacketBodyBuilder
    {
        public static byte[] BuildSuccessAck()
        {
            return new byte[] { 0x01 };
        }

        public static byte[] BuildCmdError(byte errorCode)
        {
            return new byte[] { 0x00, errorCode };
        }

        public static byte[] BuildInt32Value(int value)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(value);
            return writer.ToArray();
        }

        public static byte[] BuildZeroBytes(int count)
        {
            var writer = new GamePacketWriter();
            writer.WriteZeroBytes(count);
            return writer.ToArray();
        }
    }
}
