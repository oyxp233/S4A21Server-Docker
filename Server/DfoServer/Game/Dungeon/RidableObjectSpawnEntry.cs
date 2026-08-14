namespace DfoServer.Game.Dungeon
{
    public struct RidableObjectSpawnEntry
    {
        public int ObjectIndex;
        public int MonsterIndex;
        public int PosX;
        public int PosY;
        public int Faction;
        public int SpawnMode;
        public byte MapX;
        public byte MapY;
        public bool HasMinimapIcon;
        public int MinimapGroupIndex;
    }
}
