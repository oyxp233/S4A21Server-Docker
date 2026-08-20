using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct SelectDungeonRequest
    {
        public int DungeonId { get; }
        public byte Difficulty { get; }
        public byte Flag1 { get; }
        public byte Flag2 { get; }
        public ushort A21Sentinel { get; }
        public int TrailingLength { get; }
        public bool HasNonZeroTrailingBytes { get; }
        public byte HellPartyRequestFlag => Flag1;
        public byte HellPartyDifficultyFlag => Flag2;

        public SelectDungeonRequest(
            int dungeonId,
            byte difficulty,
            byte flag1,
            byte flag2,
            ushort a21Sentinel = 0xFFFF,
            int trailingLength = 0,
            bool hasNonZeroTrailingBytes = false)
        {
            DungeonId = dungeonId;
            Difficulty = difficulty;
            Flag1 = flag1;
            Flag2 = flag2;
            A21Sentinel = a21Sentinel;
            TrailingLength = trailingLength;
            HasNonZeroTrailingBytes = hasNonZeroTrailingBytes;
        }

        public static SelectDungeonRequest Parse(byte[] body)
        {
            if (body == null || body.Length < 9)
                throw new ArgumentException("SELECT_DUNGEON body must be at least 9 bytes.", nameof(body));

            var wireDungeonId = BitConverter.ToUInt32(body, 0);
            if (wireDungeonId > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(body), "SELECT_DUNGEON dungeon id exceeds the server domain.");

            var hasNonZeroTrailingBytes = false;
            for (var i = 9; i < body.Length; i++)
            {
                if (body[i] == 0)
                    continue;

                hasNonZeroTrailingBytes = true;
                break;
            }

            return new SelectDungeonRequest(
                (int)wireDungeonId,
                body[4],
                body[5],
                body[6],
                BitConverter.ToUInt16(body, 7),
                body.Length - 9,
                hasNonZeroTrailingBytes);
        }
    }
}
