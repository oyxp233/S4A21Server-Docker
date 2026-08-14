using System;

namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x012B CONNECT_P2P_PVP.
    ///
    /// u8 count (0..8)
    /// count * (u8 seat, u8 status)
    /// </summary>
    internal sealed class ConnectP2pPvpRequest
    {
        private readonly byte[] _statuses;

        private ConnectP2pPvpRequest(
            byte count,
            byte[] statuses)
        {
            Count = count;
            _statuses = (byte[])statuses.Clone();
        }

        internal byte Count { get; }

        internal byte GetStatus(int seat)
        {
            if (seat < 0 ||
                seat >= Game.Pvp.FreeDuelRoom.SeatCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(seat));
            }

            return _statuses[seat];
        }

        internal static bool TryParse(
            byte[] body,
            out ConnectP2pPvpRequest request)
        {
            request = null;
            if (body == null ||
                body.Length < 1 ||
                body[0] > Game.Pvp.FreeDuelRoom.SeatCount ||
                body.Length != 1 + body[0] * 2)
            {
                return false;
            }

            var statuses =
                new byte[Game.Pvp.FreeDuelRoom.SeatCount];
            var offset = 1;
            for (var index = 0;
                 index < body[0];
                 index++)
            {
                var seat = body[offset++];
                if (seat >=
                    Game.Pvp.FreeDuelRoom.SeatCount)
                {
                    return false;
                }

                statuses[seat] = body[offset++];
            }

            request =
                new ConnectP2pPvpRequest(
                    body[0],
                    statuses);
            return true;
        }
    }
}
