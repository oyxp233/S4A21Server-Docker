using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class MagicBoxOpenAckBuilder
    {
        private const int A21RewardTailSize = 23;

        public static byte[] BuildBatch(BoosterUseResult result)
        {
            result = result ?? new BoosterUseResult();
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(result.MagicBoxClientType);
            writer.WriteByte(result.DoubleRewards.Count > 0 ? (byte)1 : (byte)0);
            writer.WriteUInt16(ToUInt16(Math.Max(1, result.ConsumedSourceCount)));
            writer.WriteInt16(result.SourceSlotIndex);
            writer.WriteInt16(GetMaterialSlot(result));
            WriteRewardList(writer, GetPrimaryRewards(result), WriteBatchRewardRow);
            writer.WriteUInt16(0);
            WriteRewardList(writer, GetDoubleRewards(result), WriteBatchRewardRow);
            return writer.ToArray();
        }

        public static byte[] BuildSingle(BoosterUseResult result)
        {
            result = result ?? new BoosterUseResult();
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(result.MagicBoxClientType);
            writer.WriteByte(result.DoubleRewards.Count > 0 ? (byte)1 : (byte)0);
            writer.WriteInt16(result.SourceSlotIndex);
            writer.WriteInt16(GetMaterialSlot(result));
            WriteRewardList(writer, GetSingleRewards(result), WriteSingleRewardRow);
            return writer.ToArray();
        }

        public static byte[] BuildSingleSilentCompletion()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            // 0x00D0 handler先关闭开箱窗口，再按客户端类型0..5展示结果；保留值仅执行前者。
            writer.WriteByte(byte.MaxValue);
            writer.WriteByte(0);
            return writer.ToArray();
        }

        private static short GetMaterialSlot(BoosterUseResult result)
        {
            if (result == null || result.ConsumedMaterialCount <= 0)
                return -1;

            return result.ConsumedMaterialSlotIndex;
        }

        private static List<PackageGrantedItem> GetPrimaryRewards(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            AddAggregatedDisplayItems(items, result.DisplayRewards, result.Rewards);
            if (items.Count > 0)
                return items;

            foreach (var reward in result.Rewards)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.GrantedCount <= 0)
                    continue;

                items.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.GrantedCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                    SpecialOutcome = reward.SpecialOutcome,
                });
            }

            return items;
        }

        private static List<PackageGrantedItem> GetDoubleRewards(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            AddAggregatedDisplayItems(items, result.DoubleRewards, result.Rewards);
            return items;
        }

        private static List<PackageGrantedItem> GetSingleRewards(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            var usedGrantIndexes = new HashSet<int>();
            AddDisplayItems(items, result.DisplayRewards, result.Rewards, usedGrantIndexes);
            AddDisplayItems(items, result.DoubleRewards, result.Rewards, usedGrantIndexes);
            if (items.Count > 0)
                return items;

            foreach (var reward in result.Rewards)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.GrantedCount <= 0)
                    continue;

                items.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.GrantedCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                });
            }

            return items;
        }

        private static void AddItems(List<PackageGrantedItem> target, IEnumerable<PackageGrantedItem> source)
        {
            if (target == null || source == null)
                return;

            foreach (var reward in source)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.DisplayCount <= 0)
                    continue;

                target.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.DisplayCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                    SpecialOutcome = reward.SpecialOutcome,
                });
            }
        }

        private static void AddAggregatedDisplayItems(
            List<PackageGrantedItem> target,
            IEnumerable<PackageGrantedItem> source,
            IReadOnlyList<BoosterRewardResult> grants)
        {
            if (target == null || source == null)
                return;

            var byItemId = new Dictionary<int, PackageGrantedItem>();
            var ordered = new List<PackageGrantedItem>();
            foreach (var reward in source)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.DisplayCount <= 0)
                    continue;

                if (byItemId.TryGetValue(reward.ItemTemplateId, out var existing))
                {
                    existing.DisplayCount = AddCount(existing.DisplayCount, reward.DisplayCount);
                    if (existing.ExpireTime <= 0)
                        existing.ExpireTime = reward.ExpireTime;
                    continue;
                }

                var item = new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.DisplayCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                    SpecialOutcome = reward.SpecialOutcome,
                };
                byItemId[item.ItemTemplateId] = item;
                ordered.Add(item);
            }

            foreach (var item in ordered)
            {
                var grantIndex = FindGrantIndex(item.ItemTemplateId, grants, null);
                if (grantIndex >= 0)
                {
                    var grant = grants[grantIndex];
                    item.ListType = grant.ListType;
                    item.SlotIndex = grant.SlotIndex;
                    item.Durability = grant.Durability;
                    item.Attr = grant.Attr;
                    if (grant.ExpireTime > 0)
                        item.ExpireTime = grant.ExpireTime;
                    item.SpecialOutcome = grant.SpecialOutcome;
                }

                target.Add(item);
            }
        }

        private static void AddDisplayItems(
            List<PackageGrantedItem> target,
            IEnumerable<PackageGrantedItem> source,
            IReadOnlyList<BoosterRewardResult> grants,
            HashSet<int> usedGrantIndexes)
        {
            if (target == null || source == null)
                return;

            foreach (var reward in source)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.DisplayCount <= 0)
                    continue;

                var item = new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.DisplayCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                    SpecialOutcome = reward.SpecialOutcome,
                };

                var grantIndex = FindGrantIndex(reward.ItemTemplateId, grants, usedGrantIndexes);
                if (grantIndex >= 0)
                {
                    var grant = grants[grantIndex];
                    item.ListType = grant.ListType;
                    item.SlotIndex = grant.SlotIndex;
                    item.Durability = grant.Durability;
                    item.Attr = grant.Attr;
                    if (grant.ExpireTime > 0)
                        item.ExpireTime = grant.ExpireTime;
                    item.SpecialOutcome = grant.SpecialOutcome;
                    usedGrantIndexes?.Add(grantIndex);
                }

                target.Add(item);
            }
        }

        private static int FindGrantIndex(
            int displayItemTemplateId,
            IReadOnlyList<BoosterRewardResult> grants,
            HashSet<int> usedGrantIndexes)
        {
            if (displayItemTemplateId <= 0 || grants == null)
                return -1;

            for (var i = 0; i < grants.Count; i++)
            {
                if (usedGrantIndexes != null && usedGrantIndexes.Contains(i))
                    continue;

                var grant = grants[i];
                if (grant == null)
                    continue;

                if (grant.ItemTemplateId == displayItemTemplateId
                    || grant.SpecialOutcome?.ItemTemplateId == displayItemTemplateId)
                    return i;
            }

            return -1;
        }

        private static void WriteRewardList(GamePacketWriter writer, IReadOnlyList<PackageGrantedItem> rewards, Action<GamePacketWriter, PackageGrantedItem> writeRow)
        {
            var count = rewards != null ? Math.Min(rewards.Count, ushort.MaxValue) : 0;
            writer.WriteUInt16((ushort)count);
            for (var i = 0; i < count; i++)
                writeRow(writer, rewards[i]);
        }

        private static void WriteBatchRewardRow(GamePacketWriter writer, PackageGrantedItem reward)
        {
            WriteRewardRow(writer, reward);
        }

        private static void WriteSingleRewardRow(GamePacketWriter writer, PackageGrantedItem reward)
        {
            WriteRewardRow(writer, reward);
        }

        private static void WriteRewardRow(GamePacketWriter writer, PackageGrantedItem reward)
        {
            writer.WriteInt16(reward.SlotIndex);
            writer.WriteInt32(reward.ItemTemplateId);
            writer.WriteInt32(Math.Max(1, reward.DisplayCount));
            writer.WriteUInt16(reward.Durability);
            writer.WriteByte(reward.Attr);
            writer.WriteInt32(reward.ExpireTime);
            writer.WriteZeroBytes(A21RewardTailSize);
        }

        private static ushort ToUInt16(int value)
        {
            if (value <= 0)
                return 0;
            if (value > ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }

        private static int AddCount(int left, int right)
        {
            var value = (long)Math.Max(0, left) + Math.Max(0, right);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
