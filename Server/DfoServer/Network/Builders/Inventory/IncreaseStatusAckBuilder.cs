namespace DfoServer.Network.Builders
{
    internal static class IncreaseStatusAckBuilder
    {
        internal const int SuccessBodyLength = 12;

        private const byte NoStatusChange = 0xFF;

        internal static byte[] BuildExperienceSuccess(ushort targetUserId)
        {
            var writer = new GamePacketWriter();

            // 86JP CMD 0x001E 成功体。普通 EXP 由 NOTI 0x0025 单独下发，
            // 因此本记录必须明确表示没有状态变更。
            writer.WriteByte(0x01);           // +0x00 [u8]  指令成功。
            writer.WriteUInt16(targetUserId); // +0x01 [u16] 目标用户 ID。
            writer.WriteByte(NoStatusChange); // +0x03 [u8]  状态类型：无状态变更。
            writer.WriteUInt32(0);            // +0x04 [u32] 无状态变更时数值为零。
            writer.WriteUInt16(0);            // +0x08 [u16] 已验证的兼容尾，语义未确认，固定为零。
            writer.WriteUInt16(0);            // +0x0A [u16] 已验证的兼容尾，语义未确认，固定为零。
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);          // +0x00 [u8] 指令失败。
            writer.WriteByte(errorCode);     // +0x01 [u8] 客户端错误码选择值。
            return writer.ToArray();
        }
    }
}
