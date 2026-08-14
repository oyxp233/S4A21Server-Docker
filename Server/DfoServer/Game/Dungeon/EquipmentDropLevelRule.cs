using System;

namespace DfoServer.Game.Dungeon
{
    internal static class EquipmentDropLevelRule
    {
        public static bool TryGetAtlasGradeRange(int dungeonMinimumLevel, int dungeonBasisLevel, out int gradeMin, out int gradeMax)
        {
            gradeMin = 0;
            gradeMax = 0;

            if (dungeonMinimumLevel <= 0 && dungeonBasisLevel <= 0)
                return false;
            if (dungeonMinimumLevel <= 0)
                dungeonMinimumLevel = dungeonBasisLevel;
            if (dungeonBasisLevel <= 0)
                dungeonBasisLevel = dungeonMinimumLevel;
            if (dungeonMinimumLevel > dungeonBasisLevel)
            {
                var tmp = dungeonMinimumLevel;
                dungeonMinimumLevel = dungeonBasisLevel;
                dungeonBasisLevel = tmp;
            }

            // 装备图鉴展示的可掉落副本等级为: 装备 grade - 7 到装备 grade。
            // 副本自身按 [minimum required level, basis level] 作为等级区间，两段相交即可掉落。
            gradeMin = Math.Max(1, dungeonMinimumLevel);
            gradeMax = Math.Min(200, dungeonBasisLevel + 7);
            return gradeMax >= gradeMin;
        }
    }
}
