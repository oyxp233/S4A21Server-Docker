using System;
using System.Text;

namespace DfoServer.Network.Parsers
{
    
    
    
    
    
    
    public static class LoginRequestParser
    {
        public sealed class LoginRequest
        {
            public string MId { get; set; }
            public string PasswordHash { get; set; }
        }

        public static bool TryParse(byte[] body, out LoginRequest request)
        {
            request = null;
            if (body == null || body.Length < 8)
                return false;

            try
            {
                var offset = 0;
                if (!TryReadDStr(body, ref offset, out var mId))
                    return false;
                if (!TryReadDStr(body, ref offset, out var pwd))
                    return false;

                request = new LoginRequest
                {
                    MId = mId,
                    PasswordHash = pwd,
                };
                return !string.IsNullOrEmpty(mId);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadDStr(byte[] body, ref int offset, out string value)
        {
            value = null;
            if (offset + 4 > body.Length)
                return false;

            var length = BitConverter.ToInt32(body, offset);
            offset += 4;
            if (length < 0 || length > 256 || offset + length > body.Length)
                return false;

            value = Encoding.ASCII.GetString(body, offset, length);
            offset += length;
            return true;
        }
    }
}
