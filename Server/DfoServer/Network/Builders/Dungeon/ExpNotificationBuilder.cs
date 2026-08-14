using DfoServer.Game.Accounts;
using DfoServer.Game.Skills;

namespace DfoServer.Network.Builders
{
    public static class ExpNotificationBuilder
    {
        public const int PvpVictoryPointOffset = 0x15;
        public const int VariableEntryCountOffset = 0x32;
        public const int GrowthCapsuleExpOffset = 0x3B;
        public const int HonorLevelOffset = 0x3F;
        public const int HonorExpOffset = 0x43;
        public const int ClientReadLengthWithNoVariableEntries = 0x57;
        public const int CompatibilityTailLength = 8;
        public const int BodyLength = ClientReadLengthWithNoVariableEntries + CompatibilityTailLength;

        public static byte[] Build(byte level, uint totalExp, SkillPointProtocolState skillPoints,
            HonorLevelSummary honorLevel,
            uint pvpVictoryPointSnapshot = 0,
            uint partyRewardExp = 0, uint memberRewardExp = 0, uint fatigueBuffBonusExp = 0,
            uint seriaBlessingBonusExp = 0, uint growthContractBonusExp = 0,
            uint fatigueBurnBonusExp = 0, uint internetCafeBonusExp = 0,
            uint eliteMonsterKillBonusExp = 0,
            uint growthCapsuleExp = 0,
            uint channelBonusExp = 0)
        {
            var w = new GamePacketWriter();

            // 86JP NOTI 0x0025 客户端处理器地址 0x00D21900 按以下顺序读取。
            w.WriteByte(level);                         // +0x00 [u8]  角色等级
            w.WriteUInt32(totalExp);                    // +0x01 [u32] 角色累计 EXP
            w.WriteUInt32(partyRewardExp);              // +0x05 [u32] 队伍奖励 EXP，文本 0xA5F2
            w.WriteUInt32(memberRewardExp);             // +0x09 [u32] 队员奖励 EXP，文本 0xA5F3
            w.WriteUInt16(skillPoints.Page0Sp);         // +0x0D [u16] 技能页 0 剩余 SP
            w.WriteUInt16(skillPoints.Page1Sp);         // +0x0F [u16] 技能页 1 剩余 SP
            w.WriteUInt16(skillPoints.Page0Tp);         // +0x11 [u16] 技能页 0 剩余 TP
            w.WriteUInt16(skillPoints.Page1Tp);         // +0x13 [u16] 技能页 1 剩余 TP

            // 这是普通 EXP 以外的共享绝对状态。客户端文本 0x01CA 为“获得%d点决斗胜点”。
            // 当前普通经验生产者未实现该业务，使用默认值 0；严禁把 bonus_sp 投影到这里。
            w.WriteUInt32(pvpVictoryPointSnapshot);     // +0x15 [u32] 决斗胜点绝对值

            w.WriteUInt32(fatigueBuffBonusExp);         // +0x19 [u32] 疲劳值 Buff EXP，文本 0xA5F4
            w.WriteByte(0);                             // +0x1D [u8]  EXP 表现模式，普通路径为 0
            w.WriteUInt32(seriaBlessingBonusExp);       // +0x1E [u32] 赛丽亚的祝福 EXP，文本 0x13888
            w.WriteUInt32(growthContractBonusExp);      // +0x22 [u32] 成长之契约 EXP，文本 0x13885
            w.WriteUInt32(0);                           // +0x26 [u32] 未知，传给客户端地址 0x01340670
            w.WriteUInt32(fatigueBurnBonusExp);         // +0x2A [u32] 疲劳值燃烧 EXP，文本 0x13887
            w.WriteUInt32(internetCafeBonusExp);        // +0x2E [u32] 网吧奖励 EXP，文本 0xC00A

            // 可变项结构为 [u32 类型, u32 EXP]。本构造器固定 N=0，因此下方标注的是当前
            // 构造结果偏移；若 N>0，+0x33 及其后字段均后移 N*8。客户端循环地址：
            // 0x00D21BB0..0x00D21C06。
            w.WriteByte(0);                             // +0x32 [u8]  可变项数量 N
            w.WriteUInt32(eliteMonsterKillBonusExp);    // +0x33 [u32] 精英怪击杀 EXP，文本 0x13889
            w.WriteUInt32(0);                           // +0x37 [u32] 未知 EXP 分量，参与经验差值计算
            w.WriteUInt32(growthCapsuleExp);            // +0x3B [u32] 成长胶囊 EXP，由地址 0x00ABBC90 消费
            w.WriteUInt32(honorLevel?.HonorLevel ?? 0); // +0x3F [u32] 荣誉等级，由地址 0x0056E9D0 应用
            w.WriteUInt32(honorLevel?.HonorExp ?? 0);   // +0x43 [u32] 当前荣誉段 EXP，由地址 0x0056EA20 应用
            w.WriteUInt32(0);                           // +0x47 [u32] 极速成长 Buff EXP，文本 0x11CB6
            w.WriteUInt32(0);                           // +0x4B [u32] 未知，客户端解析后未继续使用
            w.WriteUInt32(channelBonusExp);             // +0x4F [u32] 频道奖励 EXP，文本 0xA5F6
            w.WriteUInt32(0);                           // +0x53 [u32] 赛丽亚的欢迎 EXP，文本 0xA5F7

            // N=0 时客户端在 +0x57 结束读取，即实际消费 87B。项目继续保留以下 8B，
            // 仅用于兼容现有 95B 输出，不能视为客户端 0x00D21900 的协议字段。
            w.WriteUInt32(0);                           // +0x57 [u32] 项目兼容尾，客户端不读取
            w.WriteUInt32(0);                           // +0x5B [u32] 项目兼容尾，客户端不读取

            return w.ToArray();
        }
    }
}
