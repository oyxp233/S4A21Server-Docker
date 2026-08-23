using DfoServer.Game.Characters;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Builders.Characters
{
    internal static class ClassChangeItemAckBuilder
    {
        internal static byte[] Build(ClassChangeItemResult result)
        {
            if (result != null && result.Success)
                return CommonPacketBodyBuilder.BuildSuccessAck();

            return CommonPacketBodyBuilder.BuildCmdError(
                ResolveErrorCode(result?.Status));
        }

        private static byte ResolveErrorCode(ClassChangeItemStatus? status)
        {
            switch (status)
            {
                case ClassChangeItemStatus.SourceExpired:
                    return 0xEB;
                case ClassChangeItemStatus.CooltimeActive:
                    return 0xD9;
                case ClassChangeItemStatus.LevelRejected:
                    return 0x1E;
                case ClassChangeItemStatus.InvalidState:
                case ClassChangeItemStatus.TargetUnchanged:
                    return 0x13;
                case ClassChangeItemStatus.SourceMissing:
                case ClassChangeItemStatus.SourceChanged:
                case ClassChangeItemStatus.SourceEmpty:
                case ClassChangeItemStatus.InvalidItem:
                case ClassChangeItemStatus.UsableCountLimitExceeded:
                    return 0x17;
                case ClassChangeItemStatus.InvalidLifecycle:
                case ClassChangeItemStatus.MutationFailed:
                case ClassChangeItemStatus.PersistenceFailed:
                case ClassChangeItemStatus.InvalidRequest:
                default:
                    return 0x17;
            }
        }
    }
}
