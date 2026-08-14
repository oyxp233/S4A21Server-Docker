namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// CMD 0x0038 PVP_TIME_OUT.
    ///
    /// The legacy ExtractPacket path reads exactly eight i32 values (32
    /// bytes). Those client-controlled values remain intentionally opaque;
    /// accepting the exact shape does not authorize client-owned settlement.
    /// </summary>
    internal readonly struct PvpTimeOutRequest
    {
        internal const int BodyLength = 8 * sizeof(int);

        internal static bool TryParse(
            byte[] body,
            out PvpTimeOutRequest request)
        {
            request = default;
            return body != null &&
                   body.Length == BodyLength;
        }
    }
}
