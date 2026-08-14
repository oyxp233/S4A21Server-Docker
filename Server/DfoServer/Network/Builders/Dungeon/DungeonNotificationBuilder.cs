using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class DungeonNotificationBuilder
    {
        // NOTI 28 (0x001C) DUNGEON_INFO
        public static byte[] BuildDungeonInfo(
            int dungeonId,
            byte difficulty,
            byte modeFlag = 0,
            byte bossX = 0,
            byte bossY = 0,
            byte hellPartyRoomX = 0xFF,
            byte hellPartyRoomY = 0xFF,
            byte dungeonMode = 0,
            IReadOnlyList<IReadOnlyList<(byte, byte)>> extraPairGroups = null,
            ushort hellPartyEnabled = 0x0000,
            ushort value1 = 0x000C,
            byte value2 = 0,
            byte flagA = 0,
            uint packetSeed = 0xFFFFFFFFu,
            byte paramA = 0,
            byte paramB = 0,
            byte paramC = 0,
            byte tailFlag0 = 0,
            byte tailFlag1 = 0,
            byte tailFlag2 = 0,
            uint tailReserved = 0)
        {
            var writer = new GamePacketWriter();

            writer.WriteInt16((short)dungeonId);
            writer.WriteByte(difficulty);
            writer.WriteByte(modeFlag);
            writer.WriteByte(bossX);
            writer.WriteByte(bossY);
            writer.WriteByte(hellPartyRoomX);
            writer.WriteByte(hellPartyRoomY);
            writer.WriteByte(dungeonMode);

            var groupCount = extraPairGroups == null ? 0 : extraPairGroups.Count;
            writer.WriteByte((byte)groupCount);
            for (var gi = 0; gi < groupCount; gi++)
            {
                var group = extraPairGroups[gi];
                writer.WriteByte((byte)group.Count);
                for (var pi = 0; pi < group.Count; pi++)
                {
                    var pair = group[pi];
                    writer.WriteByte(pair.Item1);
                    writer.WriteByte(pair.Item2);
                }
            }

            writer.WriteUInt16(hellPartyEnabled);
            writer.WriteUInt16(value1);
            writer.WriteByte(value2);
            writer.WriteByte(flagA);
            writer.WriteInt32(unchecked((int)packetSeed));
            writer.WriteByte(paramA);
            writer.WriteByte(paramB);
            writer.WriteByte(paramC);
            writer.WriteByte(tailFlag0);
            writer.WriteByte(tailFlag1);
            writer.WriteByte(tailFlag2);
            writer.WriteInt32(unchecked((int)tailReserved));
            return writer.ToArray();
        }

        // NOTI 678 (0x02A6) ENUM_NOTIPACKET_HELL_PARTY_MONSTER_INFO
        // 86 客户端读取：int32 count + 重复的 int32 actorIdOrKey、int32 level。
        // 当前按怪物/APC code + 对象等级发送；该包不覆盖 START_MAP 隐藏行等级。
        public static byte[] BuildHellPartyMonsterInfo(IReadOnlyList<KeyValuePair<int, int>> actorLevels)
        {
            var writer = new GamePacketWriter();
            var count = actorLevels?.Count ?? 0;
            writer.WriteInt32(count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteInt32(actorLevels[i].Key);
                writer.WriteInt32(actorLevels[i].Value);
            }

            return writer.ToArray();
        }

        // NOTI 29 (0x001D) START_MAP
        public static byte[] BuildStartMap(
            Dungeon.MazeSumInfo maze,
            ushort firstMonsterSequence,
            int randomSeed = 0,
            byte layeredRoomFlag = 0,
            byte hellPartyMode = 0,
            byte unknownAfterHellPartyMode = 0,
            uint roomStateValue = 1,
            byte roomStateFlag = 1,
            byte hellPartyFogFlag = 0,
            byte partyMemberIndex = 0xFF,
            IReadOnlyList<Game.Dungeon.PassiveObjectDropEntry> extraEntries = null,
            IReadOnlyList<Game.Dungeon.RidableObjectSpawnEntry> ridableEntries = null)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte((byte)maze.X);
            writer.WriteByte((byte)maze.Y);
            writer.WriteByte(layeredRoomFlag);
            writer.WriteInt32(randomSeed);
            writer.WriteByte(hellPartyMode);
            writer.WriteByte(unknownAfterHellPartyMode);
            writer.WriteInt32(unchecked((int)roomStateValue));
            writer.WriteByte(roomStateFlag);

            writer.WriteUInt16((ushort)maze.Index);
            writer.WriteByte((byte)maze.Monsters.Count);

            int normalIndex = 0;
            int apcIndex = 0;
            for (var i = 0; i < maze.Monsters.Count; i++)
            {
                var monster = maze.Monsters[i];
                bool isApc = monster.Type >= 5;
                var packetIndex = monster.PacketIndex.HasValue
                    ? monster.PacketIndex.Value
                    : (isApc ? apcIndex++ : normalIndex++);

                writer.WriteUInt16(monster.TemplateOrder);
                writer.WriteInt32(packetIndex);
                writer.WriteUInt16((ushort)(firstMonsterSequence + i));
                writer.WriteInt32(monster.Code);
                writer.WriteByte(monster.Level);
                writer.WriteByte(monster.Type);
                writer.WriteByte(monster.Flag0);
                writer.WriteByte(monster.Flag1);
                writer.WriteInt32(monster.ExtraState);
            }

            // 预生成建筑掉落，每项 19 字节。
            var extraCount = extraEntries?.Count ?? 0;
            writer.WriteByte((byte)extraCount);
            for (int i = 0; i < extraCount; i++)
            {
                var e = extraEntries[i];
                writer.WriteByte(e.ObjectIndex);     // +0  passive object index
                writer.WriteUInt16(e.GlobalSeq);     // +1  global sequence
                writer.WriteUInt32(ResolveTemplateId(e.Core, e.ItemId));        // +3  item template id
                writer.WriteUInt32(ResolveDropValue(e.Core, e.StackCount));    // +7  value/count
                writer.WriteUInt16(ResolveEndurance(e.Core, e.Endurance));     // +11 endurance
                writer.WriteByte(e.Core != null ? e.Core.AmplifyType : (byte)0);                 // +13 amplify type
                writer.WriteUInt16(e.Core != null ? e.Core.AmplifyValue : (ushort)0);               // +14 amplify value
                writer.WriteUInt16(0);               // +16 extended
                writer.WriteByte(0);                 // +18 extended
            }

            writer.WriteByte(hellPartyFogFlag);

            // 可骑乘对象生成列表。
            var ridableForThisRoom = new System.Collections.Generic.List<Game.Dungeon.RidableObjectSpawnEntry>();
            if (ridableEntries != null)
                foreach (var r in ridableEntries)
                    ridableForThisRoom.Add(r);

            if (ridableForThisRoom.Count > 0)
            {
                writer.WriteByte(1);                                     // 分组数量
                writer.WriteByte((byte)ridableForThisRoom.Count);        // 本组对象数量
                foreach (var r in ridableForThisRoom)
                {
                    writer.WriteInt32(r.PosX);
                    writer.WriteInt32(r.PosY);
                    writer.WriteInt32(r.ObjectIndex);
                    writer.WriteInt32(r.Faction);
                    writer.WriteInt32(r.SpawnMode);
                }
            }
            else
            {
                writer.WriteByte(0);                                     // 无可骑乘对象分组
            }

            writer.WriteByte(partyMemberIndex);

            return writer.ToArray();
        }

        public static byte[] BuildStartMapRevisit(Dungeon.MazeSumInfo maze, uint seed)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)maze.X);
            writer.WriteByte((byte)maze.Y);
            writer.WriteByte(0);                      // 分层房间标记
            writer.WriteInt32(unchecked((int)seed));
            writer.WriteByte(0);                      // 深渊模式
            writer.WriteByte(0);                      // 深渊模式后续未知字节
            writer.WriteInt32(1);                     // 房间状态值
            writer.WriteByte(0);                      // 房间状态标记，重访为 0
            writer.WriteByte(0x00);                   // 深渊雾/小地图标记
            writer.WriteByte(0xFF);                   // 队员索引
            return writer.ToArray();
        }

        // 包体长度 = 3 + dropCount * 39 + 4
        public static byte[] BuildMonsterDie(ushort monsterSeqId, IReadOnlyList<DropInfo> drops, ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(monsterSeqId);
            var dropCount = drops?.Count ?? 0;
            w.WriteByte((byte)dropCount);

            for (int i = 0; i < dropCount; i++)
            {
                var d = drops[i];
                w.WriteUInt16(d.SceneSlot);     // +0  sceneSlot
                w.WriteUInt32(ResolveTemplateId(d.Core, d.TemplateId));    // +2  templateId (0=gold)
                w.WriteByte(d.Core != null ? d.Core.Upgrade : d.UpgradeLevel);    // +6  upgradeLevel
                w.WriteUInt32(ResolveDropValue(d.Core, d.StackCount));    // +7  value/count
                w.WriteUInt16(ResolveEndurance(d.Core, d.Endurance));     // +11 endurance
                w.WriteUInt32(0);               // +13 unknown32
                w.WriteByte(d.Core != null ? d.Core.GenuineUpgrade : (byte)0);                 // +17 refineLevel
                w.WriteByte(d.Core != null ? d.Core.AmplifyType : (byte)0);                 // +18 amplify type
                w.WriteUInt16(d.Core != null ? d.Core.AmplifyValue : (ushort)0);               // +19 amplify value
                w.WriteUInt32(d.Core != null ? unchecked((uint)d.Core.EnchantCardId) : 0);               // +21 enchantCardId
                w.WriteByte(d.Core != null ? d.Core.EmblemSocketCount : (byte)0);                 // +25 socketCount
                w.WriteUInt16(0);               // +26 extra16
                w.WriteByte(0);                 // +28 listCount
                w.WriteZeroBytes(8);            // +29 tailPadding (8B)
                w.WriteUInt16(ownerActorId);    // +37 ownerActorId
            }

            // 末尾固定 4 字节
            w.WriteByte(0x00);
            w.WriteByte(0x00);
            w.WriteByte(0xFF);
            w.WriteByte(0x00);

            return w.ToArray();
        }

        private static uint ResolveTemplateId(ItemCore core, uint fallback)
        {
            return core != null && core.ItemId > 0 ? (uint)core.ItemId : fallback;
        }

        private static uint ResolveDropValue(ItemCore core, uint fallbackStackCount)
        {
            if (core == null)
                return fallbackStackCount;

            if (!core.IsEquipmentItem())
                return (uint)Math.Max(0, core.Count);

            return unchecked((uint)core.Value);
        }

        private static ushort ResolveEndurance(ItemCore core, ushort fallback)
        {
            return core != null ? core.Durability : fallback;
        }

        public static byte[] BuildEnableClearDungeon()
        {
            return new byte[] { 0x00 };
        }

        public static byte[] BuildLinkedDungeonInfo(
            int nextDungeonId,
            int difficulty)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(nextDungeonId);
            writer.WriteInt32(difficulty);
            return writer.ToArray();
        }

        public static byte[] BuildTowerOfDespairClearReward(
            uint clearTimeMilliseconds,
            int floor,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards)
        {
            const int rewardSlotCount = 10;
            var writer = new GamePacketWriter();
            writer.WriteUInt32(clearTimeMilliseconds);
            writer.WriteUInt16((ushort)Math.Clamp(floor, 1, 100));
            writer.WriteByte(rewardSlotCount);
            for (var i = 0; i < rewardSlotCount; i++)
            {
                if (rewards != null
                    && i < rewards.Count
                    && rewards[i].ItemId > 0
                    && rewards[i].StackCount > 0)
                {
                    writer.WriteInt32(rewards[i].ItemId);
                    writer.WriteInt32(rewards[i].StackCount);
                }
                else
                {
                    writer.WriteInt32(-1);
                    writer.WriteInt32(0);
                }
            }

            return writer.ToArray();
        }

        // A14 SEQUENTIAL_DUNGEON_INFO reads int32 + byte + int32.
        internal static byte[] BuildSequentialDungeonInfo(
            int configKey,
            byte progressIndex,
            int routeMask)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(configKey);
            writer.WriteByte(progressIndex);
            writer.WriteInt32(routeMask);
            return writer.ToArray();
        }

        public static byte[] BuildPlayResult(
            ushort userId,
            int clearTimeMs,
            byte rankIndex,
            byte timeBonusPoint,
            byte clientRankPoint,
            bool questMaze = false,
            bool newBestClearTime = false)
        {
            var writer = new GamePacketWriter();
            // df_game_r DisPatcher_SetPlayResult::SendResult:
            // rankIndex, clearTimeMs, timeBonusPoint, clientRankPoint,
            // then CParty::makeBestClearTimePacket.
            writer.WriteByte(rankIndex);
            writer.WriteInt32(clearTimeMs);
            writer.WriteByte(timeBonusPoint);
            writer.WriteByte(clientRankPoint);
            writer.WriteByte(questMaze ? (byte)1 : (byte)0);
            writer.WriteByte(0x01);              // member count
            writer.WriteUInt16(userId);
            writer.WriteInt32(clearTimeMs);
            writer.WriteByte(newBestClearTime ? (byte)1 : (byte)0);
            return writer.ToArray();
        }

        //
        //
        //
        // finalize (sub_1F595D0): grandTotal = expA + endValue + Σbonus
        // df_game_r CParty::clear_reward / getClearRewardBonusExp:
        // 总经验显示由通关基础经验、通关奖励字段、额外经验槽位、尾部杀怪经验共同组成。
        // 槽位：1-13 通关额外奖励，14-25 杀怪额外奖励，101-108 后置额外奖励。
        public static byte[] BuildClearDungeonReward(uint clearBaseExp, int scoreBonusExp = 0,
            uint partyClearBreakdownExp = 0,
            int avatarExp = 0, int creatureExp = 0,
            int blackDiamondExp = 0, int growthContractExp = 0,
            int monsterGrowthContractExp = 0, int adventureGroupExp = 0,
            int channelExp = 0,
            uint monsterExp = 0, int bossExp = 0, int championExp = 0, int superChampionExp = 0,
            int freeCardGold = 0, int freeCardItemId = 0, int freeCardItemCount = 0,
            int paidCardCost = 0)
        {
            var w = new GamePacketWriter();

            // === BASE BLOCK (117B = 4u32 + 1u8 + 25u32) ===
            w.WriteUInt32(clearBaseExp);
            w.WriteInt32(scoreBonusExp);
            w.WriteUInt32(partyClearBreakdownExp);
            w.WriteInt32(avatarExp);         // #4: 装扮通关奖励
            w.WriteByte(0);
            for (int i = 0; i < 25; i++)
            {
                var value = 0;
                if (i == 2) value = blackDiamondExp;       // 槽位3: 黑钻
                else if (i == 5) value = creatureExp;       // 槽位6: 宠物通关奖励
                else if (i == 7) value = adventureGroupExp; // 槽 8：冒险团通关经验
                else if (i == 9) value = growthContractExp; // 槽位10: 成长之契约
                else if (i == 18) value = monsterGrowthContractExp; // 槽位19: 杀怪成长之契约
                else if (i == 23) value = channelExp;       // 槽位24: 频道奖励
                w.WriteInt32(value);
            }

            // === ADD/MUL BONUS (2B) ===
            w.WriteByte(0);
            w.WriteByte(0);

            // === POST-BASE (32B = 8u32) ===
            for (int i = 0; i < 8; i++)
                w.WriteInt32(0);

            // === SCORE (16B = 4u32) ===
            w.WriteInt32(0);
            w.WriteInt32(championExp);
            w.WriteInt32(superChampionExp);
            w.WriteInt32(bossExp);

            // === QUEST (4B) ===
            w.WriteUInt32(0);

            // === DROPS (1B) ===
            w.WriteByte(0);

            byte freeCnt = (byte)(freeCardItemId > 0 ? 2 : 1);
            w.WriteByte(freeCnt);           // seat0.cnt
            w.WriteInt32(0);
            w.WriteInt32(freeCardGold);
            if (freeCardItemId > 0)
            {
                w.WriteInt32(freeCardItemId);
                w.WriteInt32(freeCardItemCount);
            }
            for (int i = 1; i < 8; i++)
                w.WriteByte(0);

            // === PAID-CARD COMMISSION (4B) ===
            w.WriteInt32(Math.Max(0, paidCardCost));

            // === BUFF TABLE 2 (8B) ===
            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            // === TAIL (14B) ===
            w.WriteInt32(0);                // cardItemId
            w.WriteByte(0);                 // endFlagA
            w.WriteByte(0);                 // endFlagB
            w.WriteUInt32(monsterExp);
            w.WriteInt32(0);

            return w.ToArray();             // 222B, free card has item 时会更长
        }
    }
}
