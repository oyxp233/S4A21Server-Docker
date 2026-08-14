using System;
using System.Collections.Generic;
using DfoServer.Game.DeathTower;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    // 塔专属包构建(NOTI 142/143/144/145/146)。字段定义全部来自 86JP IDA 汇编定案。
    public static class DeathTowerPacketBuilder
    {
        public const byte NormalTowerInfoModeByte = 0;
        public const byte ObservedRandomBuffType = 11;

        // NOTI 142 DEATH_TOWER_INFO (8B 固定, 双端闭环)

        public static byte[] BuildTowerInfo(
            int dungeonId,
            ushort endStage,
            byte towerInfoModeByte = NormalTowerInfoModeByte,
            byte randomBuffType = ObservedRandomBuffType)
        {
            var w = new GamePacketWriter();
            w.WriteUInt32((uint)dungeonId);
            w.WriteUInt16(endStage);
            w.WriteByte(towerInfoModeByte);
            w.WriteByte(randomBuffType);
            return w.ToArray();
        }

        // NOTI 143 START_DEATH_TOWER_MAP (变长, 汇编定案: 9B头 + 14B×怪物 + 1B + 18B×物品)
        public static byte[] BuildStageMap(
            DeathTowerSession tower,
            List<StageMonster> monsters,
            IReadOnlyList<StageTowerItem> items,
            uint randomSeed)
        {
            var w = new GamePacketWriter();
            var monsterCount = Math.Min(monsters?.Count ?? 0, byte.MaxValue);
            var itemCount = Math.Min(items?.Count ?? 0, byte.MaxValue);

            // Header 9B — currentStage 是 1-based(客户端显示层数)
            w.WriteUInt16((ushort)(tower.CurrentStage + 1));
            w.WriteUInt32(randomSeed);
            w.WriteUInt16((ushort)tower.GetCurrentMapId());
            w.WriteByte((byte)monsterCount);

            // 怪物条目 14B/条
            for (var index = 0; index < monsterCount; index++)
            {
                var m = monsters[index];
                w.WriteUInt32((uint)m.ListIndex);       // ListIndex
                w.WriteUInt16(m.MonsterUniqueId);       // MonsterUniqueId
                w.WriteUInt32((uint)m.MonsterIndex);    // MonsterIndex (模板ID)
                w.WriteByte(m.MonsterLevel);            // MonsterLevel
                w.WriteByte(m.MonsterType);             // MonsterType
                w.WriteByte(m.IsBoxMonster);            // isBoxMonster
                w.WriteByte(m.BoxIndex);                // boxIndex
            }

            // Items: 18-byte rows bound to the APC list index and stable item unique ID.
            w.WriteByte((byte)itemCount);
            for (var index = 0; index < itemCount; index++)
            {
                var item = items[index];
                w.WriteUInt32((uint)item.SourceListIndex);
                w.WriteUInt16(item.ItemUniqueId);
                w.WriteUInt32((uint)item.ItemId);
                w.WriteUInt32((uint)item.DropRate);
                w.WriteUInt32((uint)Math.Max(1, item.StackCount));
            }

            return w.ToArray();
        }

        // NOTI 144 DEATH_TOWER_STATE_RANKING. Personal and global rankings
        // remain empty until their layouts have independent protocol evidence.
        public static byte[] BuildRanking(
            int dungeonId,
            int clearedFloorCount,
            int clearTimeMilliseconds)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0);                 // flag0 (86JP新增, 语义未知, 0安全)
            w.WriteInt32(Math.Max(0, clearTimeMilliseconds));
            w.WriteInt32(Math.Max(0, clearedFloorCount));
            w.WriteByte(0);                 // flag3 (86JP新增, 语义未知, 0安全)
            w.WriteUInt32((uint)dungeonId); // dungeonIdx
            w.WriteByte(0);                 // hasMyBestRecord = false

            // 5组排行(每组: 8×空dstr + u16 + u32 + u32)
            for (int g = 0; g < 5; g++)
            {
                for (int r = 0; r < 8; r++)
                {
                    w.WriteUInt32(0);       // dstr byteCount=0 (空名)
                    w.WriteByte(0);         // byteA
                    w.WriteByte(0);         // byteB
                }
                w.WriteUInt16(0);           // groupU16
                w.WriteUInt32(0);           // groupU32A
                w.WriteUInt32(0);           // groupU32B
            }
            return w.ToArray();
        }

        // NOTI 145 DEATH_TOWER_STATE_REWARD (变长, 双端闭环: summary + 4组×{count+items})
        // Client handler (86JP DNF.exe RVA 0x008F7230):
        // u32 summary + 4 * { u8 count + count * { u32 itemId + u32 stackCount } }.
        public static byte[] BuildReward(
            int rewardExp,
            IReadOnlyList<DeathTowerRewardCandidate> candidates)
        {
            var w = new GamePacketWriter();
            w.WriteInt32(Math.Max(0, rewardExp));
            for (var groupIndex = 0; groupIndex < 4; groupIndex++)
            {
                var group = groupIndex == 0 ? candidates : null;
                var count = Math.Min(byte.MaxValue, group?.Count ?? 0);
                w.WriteByte((byte)count);
                for (var itemIndex = 0; itemIndex < count; itemIndex++)
                {
                    var item = group[itemIndex];
                    w.WriteInt32(item.ItemId);
                    w.WriteInt32(item.AddInfo);
                }
            }
            return w.ToArray();
        }

        // NOTI 146 DEATH_TOWER_STATE_EPLP (1B, 双端闭环)
        public static byte[] BuildEplp(bool allMembersHaveRequiredItem)
        {
            return new byte[] { allMembersHaveRequiredItem ? (byte)1 : (byte)0 };
        }

        public static byte[] BuildEplpCommandAck(byte state, byte option)
        {
            return new byte[] { 0x01, state, option };
        }
    }

}
