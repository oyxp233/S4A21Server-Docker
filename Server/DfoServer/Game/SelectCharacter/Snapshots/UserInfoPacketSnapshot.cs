namespace DfoServer.Game.SelectCharacter
{
    public sealed class SelectCharacterUserInfoPacketSnapshot
    {
        public byte UserInfoType { get; set; }

        public ushort GateOrCount { get; set; }

        public ushort UserId { get; set; }

        public byte[] NameBytes { get; set; } = new byte[0];

        public byte[] RemainingBytes { get; set; } = new byte[0];
    }
}
