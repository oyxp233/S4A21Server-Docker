using DfoServer.Game.DeathTower;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal static class DeathTowerEplpCommandParser
    {
        internal static bool TryParse(
            byte[] body,
            out DeathTowerEplpCommand command,
            out string error)
        {
            command = default;
            error = null;
            if (body == null || body.Length < 2)
            {
                error = "body_too_short";
                return false;
            }

            command = new DeathTowerEplpCommand(body[0], body[1]);
            if (!DeathTowerEplpCommandRules.TryResolveReturnDelay(
                    command,
                    out _,
                    out _))
            {
                error = command.State != 1
                    ? "unsupported_state"
                    : "unsupported_option";
                return false;
            }
            return true;
        }
    }
}
