namespace DfoServer.Game.Session
{
    public sealed class TownUserSnapshot
    {
        public ushort UserId { get; set; }

        public byte TownId { get; set; }

        public byte AreaId { get; set; }

        public short PosX { get; set; }

        public short PosY { get; set; }

        public byte Direction { get; set; }

        public byte State { get; set; }
    }
}
