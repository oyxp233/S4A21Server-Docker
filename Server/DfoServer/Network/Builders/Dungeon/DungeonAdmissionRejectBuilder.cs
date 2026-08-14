using DfoServer.Game.Dungeon;

namespace DfoServer.Network.Builders
{
    internal static class DungeonAdmissionRejectBuilder
    {
        internal static byte[] Build(DungeonAdmissionReject rejection)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(ResolveErrorCode(rejection.Reason));
            writer.WriteByte(ResolveContext(rejection));
            return writer.ToArray();
        }

        private static byte ResolveErrorCode(
            DungeonAdmissionRejectReason reason)
        {
            switch (reason)
            {
                case DungeonAdmissionRejectReason.MissingRequiredItem:
                    return 0x11;
                case DungeonAdmissionRejectReason.InsufficientFatigue:
                    return 0x16;
                case DungeonAdmissionRejectReason.MemberEntryLimitReached:
                case DungeonAdmissionRejectReason.MissingPermission:
                    return 0xAD;
                case DungeonAdmissionRejectReason.DailyEntryLimitReached:
                    return 0xF6;
                case DungeonAdmissionRejectReason.NotPartyLeader:
                    return 0x08;
                case DungeonAdmissionRejectReason.InvalidSelectionState:
                    return 0x13;
                case DungeonAdmissionRejectReason.DungeonNotFound:
                    return 0x15;
                case DungeonAdmissionRejectReason.Unknown:
                case DungeonAdmissionRejectReason.DungeonUnavailable:
                default:
                    return 0x01;
            }
        }

        private static byte ResolveContext(DungeonAdmissionReject rejection)
        {
            switch (rejection.Reason)
            {
                case DungeonAdmissionRejectReason.MissingRequiredItem:
                case DungeonAdmissionRejectReason.InsufficientFatigue:
                case DungeonAdmissionRejectReason.MemberEntryLimitReached:
                case DungeonAdmissionRejectReason.MissingPermission:
                    return rejection.MemberSlot;
                default:
                    return 0;
            }
        }
    }
}
