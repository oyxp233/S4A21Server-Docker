namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// CMD 0x003A RES_PVP_RANK.
    ///
    /// The live 2014 client reports an opaque 70-byte rank block. Those
    /// client-controlled values are intentionally not exposed; an exact
    /// captured packet is only a settlement acknowledgement.
    /// </summary>
    internal readonly struct PvpRankResponseRequest
    {
        internal const int BodyLength = 70;

        internal static bool TryParse(
            byte[] body,
            out PvpRankResponseRequest request)
        {
            request = default;
            return body != null &&
                   body.Length == BodyLength;
        }
    }
}
