using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct RejoinDungeonRequest
    {
        internal RejoinDungeonRequest(
            int partyId,
            ushort targetParticipantUserId)
        {
            PartyId = partyId;
            TargetParticipantUserId = targetParticipantUserId;
        }

        internal int PartyId { get; }
        internal ushort TargetParticipantUserId { get; }
    }

    internal readonly struct CancelRejoinDungeonRequest
    {
        internal CancelRejoinDungeonRequest(int partyId)
        {
            PartyId = partyId;
        }

        internal int PartyId { get; }
    }

    internal static class DungeonRejoinRequestParser
    {
        internal static bool TryParseRejoin(
            byte[] body,
            out RejoinDungeonRequest request,
            out string error)
        {
            request = default;
            error = null;
            if (body == null || body.Length != 8)
            {
                error = "invalid_body_length";
                return false;
            }

            var partyId = BitConverter.ToInt32(body, 0);
            var targetParticipant = BitConverter.ToInt32(body, 4);
            if (partyId <= 0)
            {
                error = "invalid_party_id";
                return false;
            }
            if (targetParticipant <= 0
                || targetParticipant > ushort.MaxValue)
            {
                error = "invalid_target_participant";
                return false;
            }

            request = new RejoinDungeonRequest(
                partyId,
                (ushort)targetParticipant);
            return true;
        }

        internal static bool TryParseCancel(
            byte[] body,
            out CancelRejoinDungeonRequest request,
            out string error)
        {
            request = default;
            error = null;
            if (body == null || body.Length != 4)
            {
                error = "invalid_body_length";
                return false;
            }

            var partyId = BitConverter.ToInt32(body, 0);
            if (partyId <= 0)
            {
                error = "invalid_party_id";
                return false;
            }

            request = new CancelRejoinDungeonRequest(partyId);
            return true;
        }
    }
}
