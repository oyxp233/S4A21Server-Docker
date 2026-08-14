namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x0036 SET_PVP_TEAM_MODE.
    ///
    /// u8 battleMode
    /// </summary>
    internal readonly struct SetPvpTeamModeRequest
    {
        private SetPvpTeamModeRequest(byte battleMode)
        {
            BattleMode = battleMode;
        }

        internal byte BattleMode { get; }

        internal static bool TryParse(
            byte[] body,
            out SetPvpTeamModeRequest request)
        {
            request = default;
            if (body == null ||
                body.Length != 1 ||
                body[0] < 1 ||
                body[0] > 6)
            {
                return false;
            }

            request = new SetPvpTeamModeRequest(body[0]);
            return true;
        }
    }
}
