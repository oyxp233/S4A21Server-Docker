using DfoServer.Game.Dungeon.BloodAltar;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal static class BloodAltarEplpCommandParser
    {
        internal static bool TryParse(
            byte[] body,
            out BloodAltarEplpCommand command,
            out string error)
        {
            command = default;
            error = null;
            if (body == null || body.Length < 2)
            {
                error = "body_too_short";
                return false;
            }

            command = new BloodAltarEplpCommand(body[0], body[1]);
            return true;
        }
    }
}
