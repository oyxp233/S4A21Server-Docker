using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    public struct PassiveObjectDropEntry
    {
        public byte ObjectIndex;
        public ushort GlobalSeq;
        public uint ItemId;
        public uint StackCount;
        public ushort Endurance;
        internal ItemCore Core;

        internal DropInfo ToDropInfo()
        {
            return new DropInfo
            {
                SceneSlot = GlobalSeq,
                TemplateId = Core != null ? (uint)Core.ItemId : ItemId,
                StackCount = StackCount,
                Endurance = Core != null ? Core.Durability : Endurance,
                UpgradeLevel = Core != null ? Core.Upgrade : (byte)0,
                Core = Core != null ? Core.Copy() : null,
            };
        }
    }
}
