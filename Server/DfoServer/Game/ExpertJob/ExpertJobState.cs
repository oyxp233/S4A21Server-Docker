using System.Collections.Generic;

namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobState
    {
        public int GiveUpCount { get; set; }

        public DisjointMachineState DisjointMachine { get; set; }

        public EnchanterMachineState EnchanterMachine { get; set; }

        public List<int> LearnedRecipeIds { get; } = new List<int>();
    }

    public sealed class EnchanterMachineState
    {
        public int Endurance { get; set; }
    }
}
