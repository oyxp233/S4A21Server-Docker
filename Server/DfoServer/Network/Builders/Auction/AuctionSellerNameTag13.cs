using System;
using System.Text;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>
    /// Frozen character Name encoded as NUL-terminated CP936 within 13 bytes.
    /// Client evidence: 02323125..164 and 023258F9..2592F decode the tag and compare
    /// against USERINFO0 Name (013E73F1/7403 -> 019DFB10 -> 00551940).
    /// Zero padding is this private codec's canonical format policy: the native
    /// comparison ignores bytes after NUL and does not itself reject nonzero padding.
    /// </summary>
    public static class AuctionSellerNameTag13
    {
        private static readonly Lazy<Encoding> StrictEncoding = new Lazy<Encoding>(() =>
        {
            ClientTextEncoding.EnsureInitialized();
            return Encoding.GetEncoding(ClientTextEncoding.CodePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        });

        public static bool TryEncodeFrozenRoleName(string frozenRoleName, out byte[] tag)
        {
            tag = null;
            if (string.IsNullOrEmpty(frozenRoleName) || frozenRoleName.Length > 12
                || frozenRoleName.IndexOf('\0') >= 0)
                return false;

            try
            {
                var encoding = StrictEncoding.Value;
                var bytes = encoding.GetBytes(frozenRoleName);
                if (bytes.Length > 12 || !string.Equals(encoding.GetString(bytes), frozenRoleName, StringComparison.Ordinal))
                    return false;
                tag = new byte[13];
                bytes.CopyTo(tag, 0);
                return true;
            }
            catch (EncoderFallbackException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        public static bool TryDecode(ReadOnlySpan<byte> tag, out string roleName)
        {
            roleName = string.Empty;
            if (tag.Length != 13) return false;
            var terminator = tag.IndexOf((byte)0);
            if (terminator < 1) return false;
            for (var i = terminator + 1; i < tag.Length; i++)
                if (tag[i] != 0) return false;

            try
            {
                var encoding = StrictEncoding.Value;
                var prefix = tag.Slice(0, terminator);
                var decoded = encoding.GetString(prefix);
                if (decoded.Length == 0 || !encoding.GetBytes(decoded).AsSpan().SequenceEqual(prefix))
                    return false;
                roleName = decoded;
                return true;
            }
            catch (EncoderFallbackException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }
    }
}
