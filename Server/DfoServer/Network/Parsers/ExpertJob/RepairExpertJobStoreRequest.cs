namespace DfoServer.Network.Parsers.ExpertJob
{
    internal static class RepairExpertJobStoreRequest
    {
        internal static bool IsValid(byte[] body)
            => body == null || body.Length == 0;
    }
}
