using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    public static class AreaMaterialDropProvider
    {
        

        private static readonly Dictionary<int, int> _dungeonToItem = BuildDungeonItemMap();

        private const int DropRatePercent = 25;

        private static Dictionary<int, int> BuildDungeonItemMap()
        {
            var map = new Dictionary<int, int>();
            // 亚诺法森林 (Abnoba) → 精灵气息
            for (int d = 144; d <= 149; d++) map[d] = 10088620;
            // 漂流洞穴 (DriftCave) → 浮游石原石
            for (int d = 150; d <= 155; d++) map[d] = 10088624;
            // 厄运之城 (Meltdown) → 蘑菇孢子
            for (int d = 156; d <= 160; d++) map[d] = 10088622;
            // 逆流瀑布 (FallsToTheSky) + Behemoth → 天帷巨兽的鳞片
            for (int d = 161; d <= 165; d++) map[d] = 10088626;
            // 绝望冰崖 (Icewall) → 冰石碎片
            for (int d = 166; d <= 172; d++) map[d] = 10088628;
            return map;
        }

        public static int GetAreaMaterialItem(int dungeonId)
        {
            return _dungeonToItem.TryGetValue(dungeonId, out var itemId) ? itemId : 0;
        }

        public static int RollDrop()
        {
            return Infrastructure.ServerRandom.Next(100) < DropRatePercent ? 1 : 0;
        }
    }
}
