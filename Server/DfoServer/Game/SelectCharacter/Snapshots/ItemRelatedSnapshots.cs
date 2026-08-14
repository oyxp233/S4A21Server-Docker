using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class ExpertJobInfoSnapshot
    {
        public byte State0 { get; set; }

        public byte Mode { get; set; }

        public List<int> Entries { get; } = new List<int>();

        public List<byte> CardQualificationLevels { get; } = new List<byte>();

        public int DisjointMachineGrade { get; set; }

        public int DisjointMachineEndurance { get; set; }

        public int EnchanterLevel { get; set; }

        public int EnchanterEndurance { get; set; }
    }

    public sealed class ItemLockEntrySnapshot
    {
        public byte TypeOrList { get; set; }

        public ushort ItemKeyOrSlot { get; set; }

        public byte State { get; set; }

        public int ExtraValue { get; set; }

        public bool HasExtraValue { get; set; }
    }

    public sealed class ItemLockListSnapshot
    {
        public List<ItemLockEntrySnapshot> Entries { get; } = new List<ItemLockEntrySnapshot>();
    }

    public sealed class ItemValueEntrySnapshot
    {
        public int ItemId { get; set; }

        public int Value { get; set; }
    }

    public sealed class ChampionBreakSystemSnapshot
    {
        public int KeyId { get; set; }

        public byte Mode { get; set; }

        public int Value { get; set; }
    }
}
