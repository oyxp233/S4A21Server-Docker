using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class EquipmentRegenerationRequest
    {
        public short SourceSlotIndex { get; set; }
        public ushort Mode { get; set; }
        public ushort Part { get; set; }
    }

    internal sealed class EquipmentRegenerationResult
    {
        public bool Success => ErrorCode == 0;
        public byte ErrorCode { get; set; } = 17;
        public short SourceSlotIndex { get; set; } = -1;
        public int SourceItemTemplateId { get; set; }
        public int ResultItemTemplateId { get; set; }
        public short ResultSlotIndex { get; set; } = -1;
        public ushort Mode { get; set; }
        public ushort Part { get; set; }
        public int TargetLevel { get; set; }
        public bool LegacyResult { get; set; }
        public int CandidateCount { get; set; }
        public double SelectedWeight { get; set; }
        public List<EquipmentRegenerationConsumedEntry> ConsumedEntries { get; } = new List<EquipmentRegenerationConsumedEntry>();
    }

    internal sealed class EquipmentRegenerationConsumedEntry
    {
        public short SlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
        public int Count { get; set; }
    }

    internal sealed class EquipmentRegenerationCandidate
    {
        public int ItemTemplateId { get; set; }
        public int TargetLevel { get; set; }
        public double Weight { get; set; }
        public bool Legacy { get; set; }
    }

    internal sealed class EquipmentRegenerationCandidatePool
    {
        public int TargetLevel { get; set; }
        public double LevelWeight { get; set; }
        public IReadOnlyList<EquipmentRegenerationCandidate> Candidates { get; set; }
            = Array.Empty<EquipmentRegenerationCandidate>();
    }

    internal sealed class EquipmentRegenerationMaterial
    {
        public int ItemTemplateId { get; set; }
        public int Count { get; set; }
    }
}
