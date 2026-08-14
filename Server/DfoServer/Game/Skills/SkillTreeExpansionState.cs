namespace DfoServer.Game.Skills
{
    /// <summary>
    /// 技能类型扩展状态：数据库 -1 表示未购买，0/1 表示已购买且为当前技能页；
    /// 客户端的一字节字段使用 0xFF 表示未购买。
    /// </summary>
    public static class SkillTreeExpansionState
    {
        // PVF item 821（技能类型扩展券）购买后立即生效，不进入背包。
        public const int ExpansionItemTemplateId = 821;
        public const int LockedDatabaseValue = -1;
        public const byte LockedWireValue = 0xFF;

        public static bool IsUnlocked(byte wireValue)
        {
            return wireValue <= 1;
        }

        public static byte FromDatabase(int databaseValue)
        {
            return databaseValue == 0 || databaseValue == 1
                ? (byte)databaseValue
                : LockedWireValue;
        }

        public static int ToDatabase(byte wireValue)
        {
            return IsUnlocked(wireValue) ? wireValue : LockedDatabaseValue;
        }
    }
}
