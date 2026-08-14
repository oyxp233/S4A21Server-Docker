namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x0034 SET_PVP_SEAT_STATE.
    ///
    /// u8 seatIndex
    /// u8 seatState
    /// </summary>
    internal readonly struct SetPvpSeatStateRequest
    {
        private SetPvpSeatStateRequest(
            byte seat,
            byte seatState)
        {
            Seat = seat;
            SeatState = seatState;
        }

        internal byte Seat { get; }

        internal byte SeatState { get; }

        internal static bool TryParse(
            byte[] body,
            out SetPvpSeatStateRequest request)
        {
            request = default;
            if (body == null ||
                body.Length != 2 ||
                body[0] >= Game.Pvp.FreeDuelRoom.SeatCount ||
                !Game.Pvp.FreeDuelRoom.IsSupportedSeatState(body[1]))
            {
                return false;
            }

            request = new SetPvpSeatStateRequest(
                body[0],
                body[1]);
            return true;
        }
    }
}
