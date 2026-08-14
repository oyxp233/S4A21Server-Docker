namespace DfoServer.Network.Parsers
{
    internal static class SecretShopOpenCloseRequest
    {
        internal static bool TryParse(byte[] body, out bool open)
        {
            open = false;
            if (body == null || body.Length != 1 || body[0] > 1)
                return false;
            open = body[0] == 1;
            return true;
        }
    }
}
