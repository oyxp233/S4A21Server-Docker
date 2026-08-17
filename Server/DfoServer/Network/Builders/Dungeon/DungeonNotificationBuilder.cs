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
        // A21 固定 32B 布局。A12 的可变 pair/group 字段不再写入；当前
        // 客户端抓包显示 offset 6/7 为 boss 坐标，offset 12 为固定 1，
        // offset 18-21 为深渊房间坐标，尾部保持保留零值。
        public static byte[] BuildDungeonInfo(
            int dungeonId,
            byte difficulty,
            byte mazeIndex = 0,
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

            writer.WriteInt16((short)dungeonId);       // +0
            writer.WriteByte(difficulty);              // +2
            writer.WriteUInt16(0);                     // +3 reserved
            writer.WriteByte(mazeIndex);               // +5 selected maze index
            writer.WriteByte(bossX);                   // +6
            writer.WriteByte(bossY);                   // +7
            writer.WriteInt32(0);                      // +8 reserved
            writer.WriteInt32(1);                      // +12 A21 fixed state marker
            writer.WriteUInt16(0);                     // +16 reserved
            writer.WriteByte(hellPartyRoomX);           // +18
            writer.WriteByte(hellPartyRoomY);           // +19
            writer.WriteUInt16(0xFFFF);                 // +20..21 reserved sentinel
            writer.WriteZeroBytes(10);                 // +22..31 reserved
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
            byte hellPartyMode = 2,
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
            writer.WriteUInt16(0);                    // A21 reserved field at +16
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
                writer.WriteByte(0);                  // A21 actor record extension
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
            writer.WriteByte(2);                      // A21 标准副本模式标记
            writer.WriteByte(0);                      // 深渊模式后续未知字节
            writer.WriteInt32(1);                     // 房间状态值
            writer.WriteByte(0);                      // 房间状态标记，重访为 0
            writer.WriteByte(0x00);                   // 深渊雾/小地图标记
            writer.WriteByte(0xFF);                   // 队员索引
            return writer.ToArray();
        }

        // A21 NOTI 0x0026 body length = 3 + dropCount * 48 + 4.
        public static byte[] BuildMonsterDie(ushort monsterSeqId, IReadOnlyList<DropInfo> drops, ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(monsterSeqId);
            var dropCount = drops?.Count ?? 0;
            w.WriteByte((byte)dropCount);

            var dropGroupId = ResolveDropGroupId(drops);

            for (int i = 0; i < dropCount; i++)
            {
                var d = drops[i];
                w.WriteUInt16(d.SceneSlot);     // +0  sceneSlot
                w.WriteUInt32(ResolveTemplateId(d.Core, d.TemplateId));    // +2  templateId (0=gold)
                w.WriteByte(d.Core != null ? d.Core.Upgrade : d.UpgradeLevel);    // +6  upgradeLevel
                // Ground-drop records carry the amount visible on the map.  For
                // equipment this is the stack count (normally 1), not the
                // inventory instance UID stored in ItemCore.Value.  A21's
                // DIE_MONSTER reader consumes this field before it registers the
                // local drop object used by GET_ITEM/pickup presentation.
                w.WriteUInt32(d.StackCount);                              // +7  value/count
                w.WriteZeroBytes(5);            // +11 reserved
                w.WriteUInt32(dropGroupId);     // +16 A21 drop-group timestamp/id
                w.WriteZeroBytes(24);           // +20 reserved
                w.WriteUInt16(ownerActorId);    // +44 ownerActorId
                w.WriteUInt16(0);                // +46 reserved
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

        private static uint ResolveDropGroupId(IReadOnlyList<DropInfo> drops)
        {
            if (drops == null)
                return 0;

            for (var index = 0; index < drops.Count; index++)
            {
                if (drops[index].DropGroupId != 0)
                    return drops[index].DropGroupId;
            }

            return 0;
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
            int paidCardCost = 0,
            IReadOnlyList<DungeonObjectExperienceEntry> objectExperienceEntries = null)
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
            // A21 在此区后读取 1B 条目数；旧版把 score 四元组写在这里，
            // 会让客户端把 score 的首字节当成条目数。抓包显示第 7 个
            // 后置槽是 boss/champion/super-champion 经验合计，其余未知槽保持 0。
            var specialMonsterExp = SaturatingSum(
                bossExp,
                championExp,
                superChampionExp);
            for (var i = 0; i < 6; i++)
                w.WriteInt32(0);
            w.WriteInt32(specialMonsterExp);
            w.WriteInt32(0);

            // === RESERVED (4B) ===
            w.WriteUInt32((uint)Math.Max(0, superChampionExp));

            // === OBJECT/MONSTER EXPERIENCE ENTRIES ===
            var entries = objectExperienceEntries
                ?? Array.Empty<DungeonObjectExperienceEntry>();
            if (entries.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(objectExperienceEntries),
                    "A21 CLEAR_DUNGEON_REWARD supports at most 255 entries.");

            w.WriteByte((byte)entries.Count);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteByte(0);
            foreach (var entry in entries)
            {
                w.WriteUInt32(entry.ObjectKey);
                w.WriteUInt32(entry.Experience);
            }

            // === CARD/BUFF/TAIL (A21 fixed 115B when no bonus item) ===
            w.WriteByte(0);                    // reserved before free-card data

            byte freeCnt = (byte)(freeCardItemId > 0 ? 2 : 1);
            w.WriteByte(freeCnt);
            w.WriteInt32(0);                    // free-card item id
            w.WriteInt32(freeCardGold);
            if (freeCardItemId > 0)
            {
                w.WriteInt32(freeCardItemId);
                w.WriteInt32(freeCardItemCount);
            }

            // Seven fixed 9B card-seat entries: flag + item id + count.
            for (var i = 0; i < 7; i++)
            {
                w.WriteByte(1);
                w.WriteInt32(0);
                w.WriteInt32(0);
            }

            w.WriteInt32(Math.Max(0, paidCardCost));

            for (int i = 0; i < 8; i++)
                w.WriteByte(0);
            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            w.WriteInt32(0);                // tail card item id
            w.WriteByte(0);                 // end flag A
            w.WriteByte(0);                 // end flag B
            w.WriteUInt32(0);               // A21 sample tail monster-exp field
            w.WriteUInt32((uint)specialMonsterExp); // reserved/summary experience field
            for (var i = 0; i < 8; i++)
                w.WriteByte(0);

            return w.ToArray();
        }

        private static int SaturatingSum(int first, int second, int third)
        {
            var value = (long)Math.Max(0, first)
                + Math.Max(0, second)
                + Math.Max(0, third);
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
