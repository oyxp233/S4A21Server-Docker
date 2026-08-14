namespace DfoServer.Game.DeathTower
{
    public struct StageMonster
    {
        public int ListIndex;
        public ushort MonsterUniqueId;
        public int MonsterIndex;
        public byte MonsterLevel;
        public byte MonsterType;
        public byte IsBoxMonster;
        public byte BoxIndex;
    }

    public struct StageTowerItem
    {
        public int SourceListIndex;
        public ushort SourceMonsterUniqueId;
        public ushort ItemUniqueId;
        public int ItemId;
        public int DropRate;
        public int StackCount;
    }
}
