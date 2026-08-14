using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class Unknown725Snapshot
    {
        public int ParamA { get; set; }

        public int ModeOrState { get; set; }

        public int ContentId { get; set; }

        public int ParamB { get; set; }
    }

    public sealed class Unknown730EntrySnapshot
    {
        public int EntryId { get; set; }

        public int SentinelOrValue { get; set; }

        public int Flag { get; set; }
    }

    public sealed class Unknown730Snapshot
    {
        public List<Unknown730EntrySnapshot> Entries { get; } = new List<Unknown730EntrySnapshot>();
    }
}
