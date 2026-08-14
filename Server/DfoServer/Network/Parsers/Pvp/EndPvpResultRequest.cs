namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// CMD 0x0039 END_PVP_RESULT is an empty acknowledgement.
    /// </summary>
    internal readonly struct EndPvpResultRequest
    {
        internal static bool TryParse(
            byte[] body,
            out EndPvpResultRequest request)
        {
            request = default;
            // EnhancedClientSession represents a zero-length wire payload as
            // null. Unit callers may still supply Array.Empty<byte>().
            return body == null || body.Length == 0;
        }
    }
}
