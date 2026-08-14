using System;
using System.Collections.Generic;

namespace DfoServer.Game.ExpertJob
{
    public sealed class EnchanterStoreState
    {
        public int Endurance { get; set; }

        public IReadOnlyList<byte> CardQualificationLevels { get; set; } = Array.Empty<byte>();
    }
}
