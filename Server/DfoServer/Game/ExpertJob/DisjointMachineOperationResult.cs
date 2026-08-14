using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class DisjointMachineOperationResult
    {
        internal byte ErrorCode { get; set; }
        internal DisjointItemResult DisjointResult { get; set; }
        internal int RequesterGold { get; set; }
        internal int OwnerGold { get; set; }
        internal int Endurance { get; set; }
        internal int ExperienceGain { get; set; }
    }
}
