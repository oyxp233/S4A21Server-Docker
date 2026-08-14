using System;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonAdmissionRejectReason : byte
    {
        Unknown = 0,
        DungeonUnavailable = 1,
        InvalidSelectionState = 2,
        MissingRequiredItem = 3,
        InsufficientFatigue = 4,
        MemberEntryLimitReached = 5,
        DailyEntryLimitReached = 6,
        MissingPermission = 7,
        NotPartyLeader = 8,
        DungeonNotFound = 9,
    }

    internal readonly struct DungeonAdmissionReject
    {
        private const byte MaxMemberSlot = 7;

        private DungeonAdmissionReject(
            DungeonAdmissionRejectReason reason,
            byte memberSlot)
        {
            Reason = reason;
            MemberSlot = memberSlot;
        }

        internal DungeonAdmissionRejectReason Reason { get; }
        internal byte MemberSlot { get; }

        internal static DungeonAdmissionReject Unknown =>
            new DungeonAdmissionReject(
                DungeonAdmissionRejectReason.Unknown,
                memberSlot: 0);

        internal static DungeonAdmissionReject DungeonUnavailable =>
            new DungeonAdmissionReject(
                DungeonAdmissionRejectReason.DungeonUnavailable,
                memberSlot: 0);

        internal static DungeonAdmissionReject InvalidSelectionState =>
            new DungeonAdmissionReject(
                DungeonAdmissionRejectReason.InvalidSelectionState,
                memberSlot: 0);

        internal static DungeonAdmissionReject DailyEntryLimitReached =>
            new DungeonAdmissionReject(
                DungeonAdmissionRejectReason.DailyEntryLimitReached,
                memberSlot: 0);

        internal static DungeonAdmissionReject NotPartyLeader =>
            new DungeonAdmissionReject(
                DungeonAdmissionRejectReason.NotPartyLeader,
                memberSlot: 0);

        internal static DungeonAdmissionReject DungeonNotFound =>
            new DungeonAdmissionReject(
                DungeonAdmissionRejectReason.DungeonNotFound,
                memberSlot: 0);

        internal static DungeonAdmissionReject MissingRequiredItem(
            byte memberSlot)
            => ForMember(
                DungeonAdmissionRejectReason.MissingRequiredItem,
                memberSlot);

        internal static DungeonAdmissionReject InsufficientFatigue(
            byte memberSlot)
            => ForMember(
                DungeonAdmissionRejectReason.InsufficientFatigue,
                memberSlot);

        internal static DungeonAdmissionReject MemberEntryLimitReached(
            byte memberSlot)
            => ForMember(
                DungeonAdmissionRejectReason.MemberEntryLimitReached,
                memberSlot);

        internal static DungeonAdmissionReject MissingPermission(
            byte memberSlot)
            => ForMember(
                DungeonAdmissionRejectReason.MissingPermission,
                memberSlot);

        private static DungeonAdmissionReject ForMember(
            DungeonAdmissionRejectReason reason,
            byte memberSlot)
        {
            if (memberSlot > MaxMemberSlot)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(memberSlot));
            }

            return new DungeonAdmissionReject(reason, memberSlot);
        }
    }
}
