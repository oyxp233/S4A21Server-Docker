namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// CMD 0x0070 PVP_REQUEST_FIGHT is the empty relay-battle space-key
    /// request used to toggle participation in the next bout.
    /// </summary>
    internal readonly struct PvpRequestFightRequest
    {
        internal static bool TryParse(
            byte[] body,
            out PvpRequestFightRequest request)
        {
            request = default;
            return body == null || body.Length == 0;
        }
    }
}
