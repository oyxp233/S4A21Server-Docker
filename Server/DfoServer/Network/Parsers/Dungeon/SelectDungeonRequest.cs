using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct SelectDungeonRequest
    {
        public ushort DungeonId { get; }
        public byte Difficulty { get; }
        public byte Flag1 { get; }
        public byte Flag2 { get; }
        public byte A21Reserved0 { get; }
        public byte A21Reserved1 { get; }
        public byte HellPartyRequestFlag => Flag1;
        public byte HellPartyDifficultyFlag => Flag2;

        public SelectDungeonRequest(
            ushort dungeonId,
            byte difficulty,
            byte flag1,
            byte flag2,
            byte a21Reserved0 = 0,
            byte a21Reserved1 = 0)
        {
            DungeonId = dungeonId;
            Difficulty = difficulty;
            Flag1 = flag1;
            Flag2 = flag2;
            A21Reserved0 = a21Reserved0;
            A21Reserved1 = a21Reserved1;
        }

        public static SelectDungeonRequest Parse(byte[] body)
        {
            if (body == null || body.Length < 5)
                throw new ArgumentException("SELECT_DUNGEON body must be at least 5 bytes.", nameof(body));

            var dungeonId = BitConverter.ToUInt16(body, 0);
            return new SelectDungeonRequest(
                dungeonId,
                body[2],
                body[3],
                body[4],
                body.Length > 5 ? body[5] : (byte)0,
                body.Length > 6 ? body[6] : (byte)0);
        }
    }
}
