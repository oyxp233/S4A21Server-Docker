namespace DfoServer.Network.Parsers.ExpertJob
{
    internal static class GiveupExpertJobRequest
    {
        // The 86 client sends CMD 0x00EF without a request payload.
        internal static bool IsValid(byte[] body)
            => body == null || body.Length == 0;
    }
}
