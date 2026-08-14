using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct SelectDungeonRequest
    {
        public ushort DungeonId { get; }
        public byte Difficulty { get; }
        public byte Flag1 { get; }
        public byte Flag2 { get; }
        public byte HellPartyRequestFlag => Flag1;
        public byte HellPartyDifficultyFlag => Flag2;

        public SelectDungeonRequest(ushort dungeonId, byte difficulty, byte flag1, byte flag2)
        {
            DungeonId = dungeonId;
            Difficulty = difficulty;
            Flag1 = flag1;
            Flag2 = flag2;
        }

        public static SelectDungeonRequest Parse(byte[] body)
        {
            if (body == null || body.Length < 5)
                throw new ArgumentException("SELECT_DUNGEON body must be at least 5 bytes.", nameof(body));

            var dungeonId = BitConverter.ToUInt16(body, 0);
            return new SelectDungeonRequest(dungeonId, body[2], body[3], body[4]);
        }
    }
}
