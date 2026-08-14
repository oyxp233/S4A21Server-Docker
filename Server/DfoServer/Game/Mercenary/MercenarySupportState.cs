namespace DfoServer.Game.Mercenary
{
    // 单个角色的支援兵槽位选择状态。
    public sealed class MercenarySupportState
    {
        // 支援兵状态的服务端单例主键，不是客户端 wire slot。
        public const byte SingletonStateKey = 0;

        public int OwnerCharacterId { get; set; }
        public byte Slot { get; set; }
        public int SupportCharacterId { get; set; }
        public ushort SkillId { get; set; }
        // 历史列名；保存完整 PVF ComboIndex，用于状态校验。
        public ushort StrikerSkillId { get; set; }
    }
}
