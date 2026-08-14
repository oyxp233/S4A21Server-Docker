namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x0035 SET_PVP_READY_STATE.
    ///
    /// u8 ready (zero=false, non-zero=true)
    /// </summary>
    internal readonly struct SetPvpReadyStateRequest
    {
        private SetPvpReadyStateRequest(bool isReady)
        {
            IsReady = isReady;
        }

        internal bool IsReady { get; }

        internal static bool TryParse(
            byte[] body,
            out SetPvpReadyStateRequest request)
        {
            request = default;
            if (body == null || body.Length != 1)
                return false;

            request =
                new SetPvpReadyStateRequest(
                    body[0] != 0);
            return true;
        }
    }
}
