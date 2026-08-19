namespace DfoServer.Network.Builders
{
    internal static class LevelUpTicketAckBuilder
    {
        internal const int SuccessBodyLength = 2;

        internal static byte[] BuildSuccess()
        {
            // 真实 86 A21 C1/01A2 成功回包是短体 00 00；
            // 等级、经验和任务列表以后续 NOTI 快照为准。
            return new byte[] { 0x00, 0x00 };
        }

        internal static byte[] BuildError(byte errorCode)
            => new byte[] { 0x00, errorCode };
    }
}
