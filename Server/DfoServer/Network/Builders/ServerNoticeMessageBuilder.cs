using System.Text;

namespace DfoServer.Network.Builders
{
    public static class ServerNoticeMessageBuilder
    {
        public static byte[] BuildRaidNotice(string message, byte mode = 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteRawDstr(Encoding.GetEncoding(936).GetBytes(message ?? string.Empty));
            return writer.ToArray();
        }

        internal static string DecodeRaidNoticeName(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(936).GetString(bytes).TrimEnd('\0');
            }
        }

        public static byte[] Build(string message, byte mode = 0)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteClientDstr(message ?? string.Empty);
            return writer.ToArray();
        }
    }
}
