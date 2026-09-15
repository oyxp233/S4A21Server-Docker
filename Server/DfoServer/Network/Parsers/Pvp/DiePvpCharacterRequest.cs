using System;

namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// A21 DIE_PVP_CHARACTER: victim UID, attacker UID, then a boolean.
    /// 1838FF0 reads the attacker identity from the victim's damage source;
    /// the boolean is a separate actor-property comparison, not kill permission.
    /// The compatible two-byte form contains only the victim UID.
    /// </summary>
    internal readonly struct DiePvpCharacterRequest
    {
        private DiePvpCharacterRequest(ushort reportedDeadUserId, ushort? reportedKillerUserId)
        {
            ReportedDeadUserId = reportedDeadUserId;
            ReportedKillerUserId = reportedKillerUserId;
        }

        internal ushort ReportedDeadUserId { get; }
        internal ushort? ReportedKillerUserId { get; }

        internal static bool TryParse(
            byte[] body,
            out DiePvpCharacterRequest request)
        {
            request = default;
            if (body == null ||
                body.Length != 2 &&
                body.Length != 5)
            {
                return false;
            }
            if (body.Length == 5 && body[4] > 1)
                return false;

            request =
                new DiePvpCharacterRequest(
                    BitConverter.ToUInt16(body, 0),
                    body.Length == 5 ? BitConverter.ToUInt16(body, 2) : (ushort?)null);
            return true;
        }
    }
}
