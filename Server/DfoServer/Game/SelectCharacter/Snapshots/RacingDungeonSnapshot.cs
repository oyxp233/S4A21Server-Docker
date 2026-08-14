using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class RacingDungeonEntrySnapshot
    {
        public uint TrackLikeId { get; set; }

        public uint ValueA { get; set; }

        public uint ValueB { get; set; }

        public uint TargetValue => ValueA;

        public uint RemainingValue => ValueB;
    }

    public sealed class RacingDungeonGroupSnapshot
    {
        public uint GroupId { get; set; }

        public List<RacingDungeonEntrySnapshot> Entries { get; } = new List<RacingDungeonEntrySnapshot>();
    }
}
