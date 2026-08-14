using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Builders
{
    public static class InvestItemAmplifyOptionAckBuilder
    {
        public static byte[] BuildSuccess(InvestItemAmplifyOptionResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)result.Request.Action);
            writer.WriteInt16(result.MaterialSlotIndex);
            writer.WriteInt32(Math.Max(0, result.MaterialRemainingCount));
            writer.WriteInt16(result.TargetSlotIndex);
            writer.WriteByte(result.AmplifyType);
            writer.WriteUInt16(result.AmplifyValue);
            if (result.Request.Action == InvestItemAmplifyOptionAction.PureGold)
                writer.WriteByte(result.AmplifyLevel);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            // 86 客户端 0x00CD 失败分支读取 status 后紧跟的错误码。
            return new[] { (byte)0x00, errorCode };
        }
    }
}
