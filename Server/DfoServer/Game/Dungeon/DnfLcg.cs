namespace DfoServer.Game.Dungeon
{
    
    
    
    
    
    public sealed class DnfLcg
    {
        private uint _seed;

        public DnfLcg(uint seed) => _seed = seed;

        public uint Seed => _seed;

        public int Next()
        {
            uint v2 = _seed * 1103515245u + 12345u;
            uint v3 = v2 * 1103515245u + 12345u;
            uint v4 = v3 * 1103515245u + 12345u;
            _seed = v4;

            int hi2 = (int)((v2 >> 16) & 0x7FF);
            int hi3 = (int)((v3 >> 16) & 0x3FF);
            int hi4 = (int)((v4 >> 16) & 0x3FF);
            return ((hi2 << 10) ^ hi3) << 10 | hi4;
        }

        public int Next(int max)
        {
            if (max <= 0) return Next();
            return Next() % max;
        }
    }
}
