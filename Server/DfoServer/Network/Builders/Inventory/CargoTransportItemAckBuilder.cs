using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders
{
    internal static class CargoTransportItemAckBuilder
    {
        internal static byte[] Build(CargoTransportStoneResult result)
        {
            if (result == null || !result.Success)
                return BuildError(result);

            return Build(
                result?.Request,
                result.AckRemainingStoneCount,
                result.AckParameter,
                result.AckMode);
        }

        internal static byte[] BuildError(CargoTransportStoneRequest request)
        {
            return CommonPacketBodyBuilder.BuildCmdError(0x07);
        }

        private static byte[] BuildError(CargoTransportStoneResult result)
        {
            return CommonPacketBodyBuilder.BuildCmdError(ResolveErrorCode(result?.Status));
        }

        private static byte[] Build(
            CargoTransportStoneRequest request,
            int remainingStoneCount,
            int parameter,
            byte mode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(ToUInt16(request?.StoneSlotIndex ?? 0));
            writer.WriteUInt16(ToUInt16(request?.TargetSlotIndex ?? 0));
            writer.WriteUInt16(ToUInt16(remainingStoneCount));
            writer.WriteUInt16(ToUInt16(parameter));
            writer.WriteByte(mode);
            return writer.ToArray();
        }

        private static byte ResolveErrorCode(CargoTransportStoneStatus? status)
        {
            switch (status)
            {
                case CargoTransportStoneStatus.AccountCargoFull:
                    return 0x04;
                case CargoTransportStoneStatus.TargetLocked:
                    return 0xD6;
                case CargoTransportStoneStatus.SourceMissing:
                case CargoTransportStoneStatus.SourceChanged:
                case CargoTransportStoneStatus.SourceEmpty:
                case CargoTransportStoneStatus.UsableCountLimitExceeded:
                    return 0x16;
                case CargoTransportStoneStatus.TargetMissing:
                    return 0x11;
                case CargoTransportStoneStatus.InvalidStone:
                case CargoTransportStoneStatus.InvalidLifecycle:
                case CargoTransportStoneStatus.CooltimeActive:
                case CargoTransportStoneStatus.TargetCharacterMissing:
                case CargoTransportStoneStatus.TargetInvalidKind:
                case CargoTransportStoneStatus.TargetNotAllowed:
                case CargoTransportStoneStatus.SourceExpired:
                case CargoTransportStoneStatus.MutationFailed:
                case CargoTransportStoneStatus.MailFailed:
                case CargoTransportStoneStatus.InvalidRequest:
                default:
                    return 0x07;
            }
        }

        private static ushort ToUInt16(int value)
        {
            return (ushort)Math.Max(0, Math.Min(ushort.MaxValue, value));
        }
    }
}
