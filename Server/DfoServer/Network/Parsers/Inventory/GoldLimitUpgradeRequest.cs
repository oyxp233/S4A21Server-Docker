namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class GoldLimitUpgradeRequest
    {
        // Current A21 capture: 0x03BA body is always exactly 15 bytes.
        // The individual fields are not needed by the server-side upgrade rule.
        internal const int WireBodyLength = 15;

        internal static bool TryParse(byte[] body, out GoldLimitUpgradeRequest request)
        {
            request = null;
            if (body == null || body.Length != WireBodyLength)
                return false;

            request = new GoldLimitUpgradeRequest();
            return true;
        }
    }
}
