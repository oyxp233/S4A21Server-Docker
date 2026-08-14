using System;

namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobStoreSession
    {
        public Guid OwnerSessionId { get; set; }

        public int OwnerCharacterId { get; set; }

        public ushort OwnerUserId { get; set; }

        public byte ExpertJobType { get; set; }

        public ExpertJobStoreKind Kind { get; set; }

        public byte[] NameBytes { get; set; } = Array.Empty<byte>();

        public int Cost { get; set; }

        public DisjointMachineState DisjointMachine { get; set; }

        public EnchanterStoreState Enchanter { get; set; }

        public byte TownId { get; set; }

        public byte AreaId { get; set; }

        public short PositionX { get; set; }

        public short PositionY { get; set; }

        public short Direction { get; set; }
    }
}
