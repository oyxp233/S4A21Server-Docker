using System;

namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// CMD 0x0037 DIE_PVP_CHARACTER.
    ///
    /// The leading u16 is the reporting (dead) character's town/PvP user id,
    /// not the killer id.  The current 2016 client sends three additional
    /// opaque bytes. Keep the accepted shapes explicit so arbitrary trailing
    /// data cannot enter settlement state.
    /// </summary>
    internal readonly struct DiePvpCharacterRequest
    {
        private DiePvpCharacterRequest(ushort reportedDeadUserId)
        {
            ReportedDeadUserId = reportedDeadUserId;
        }

        internal ushort ReportedDeadUserId { get; }

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

            request =
                new DiePvpCharacterRequest(
                    BitConverter.ToUInt16(body, 0));
            return true;
        }
    }
}
