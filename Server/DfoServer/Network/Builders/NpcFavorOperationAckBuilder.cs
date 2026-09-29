using DfoServer.Game.NpcFavor;
using System;

namespace DfoServer.Network.Builders
{
    internal static class NpcFavorOperationAckBuilder
    {
        internal const byte SuccessResult = 1;
        internal const byte GiftOperation = 0;
        internal const byte DailyGiftLimitErrorCode = 0x66;
        internal const int GiftBodyLength = 13;

        internal static bool TryBuildGiftFailure(
            NpcFavorGiftError error,
            out byte[] body)
        {
            // DNF.exe 0x01133FDD dispatches REQUEST_NPC_FAVOR_OPERATION
            // failures by the byte following the common result byte. Error
            // 0x66 resolves through dstr resource 65020 (0xFDFC) to:
            // "已超过每天可赠送的次数上限。"
            if (error == NpcFavorGiftError.NpcDailyLimit)
            {
                body = CommonPacketBodyBuilder.BuildCmdError(
                    DailyGiftLimitErrorCode);
                return true;
            }

            body = null;
            return false;
        }

        internal static byte[] BuildGift(
            int npcId,
            int appliedFavorPointDelta,
            int favorPoint,
            NpcFavorGiftDisposition disposition)
        {
            if (npcId < 0)
                throw new ArgumentOutOfRangeException(nameof(npcId));
            if (favorPoint < 0)
                throw new ArgumentOutOfRangeException(nameof(favorPoint));

            // DNF.exe SHA-256 1F8244D3F7309BBE2FDE4299E1863A894F6B28D5707BF01732980585B20B00DD
            // registers CMD 0x032A response handler at VA 0x01133D90.
            // The command dispatcher consumes the leading success result byte.
            // The successful gift branch then reads the remaining fields in this
            // exact order.
            var writer = new GamePacketWriter();
            writer.WriteByte(SuccessResult);
            writer.WriteByte(GiftOperation);
            writer.WriteInt32(npcId);
            writer.WriteUInt16((ushort)Math.Clamp(
                appliedFavorPointDelta,
                0,
                ushort.MaxValue));
            writer.WriteInt32(favorPoint);
            writer.WriteByte((byte)disposition);
            return writer.ToArray();
        }
    }
}
