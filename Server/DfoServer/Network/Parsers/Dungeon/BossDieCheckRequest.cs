namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct BossDieCheckRequest
    {
        public BossDieCheckRequest(ushort userId, ushort bossSequence)
        {
            UserId = userId;
            BossSequence = bossSequence;
        }

        public ushort UserId { get; }
        public ushort BossSequence { get; }

        public static bool TryParse(byte[] body, out BossDieCheckRequest request)
        {
            request = default;
            if (body == null || body.Length < 4)
                return false;

            request = new BossDieCheckRequest(
                (ushort)(body[0] | (body[1] << 8)),
                (ushort)(body[2] | (body[3] << 8)));
            return true;
        }
    }
}
