namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobStoreCreateCommand
    {
        public ExpertJobStoreKind Kind { get; set; }

        public byte[] NameBytes { get; set; } = System.Array.Empty<byte>();

        public int Cost { get; set; }

        public short PositionX { get; set; }

        public short PositionY { get; set; }

        public short Direction { get; set; }
    }
}
