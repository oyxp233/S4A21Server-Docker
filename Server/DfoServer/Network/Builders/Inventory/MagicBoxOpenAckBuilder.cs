using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class MagicBoxOpenAckBuilder
    {
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

            AddItems(items, result.DisplayRewards);
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
                    Durability = 0,
                });
            }

            return items;
        }

        private static List<PackageGrantedItem> GetDoubleRewards(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            AddItems(items, result.DoubleRewards);
            return items;
        }

        private static List<PackageGrantedItem> GetSingleRewards(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
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
                    Durability = 0,
                });
            }

            if (items.Count > 0)
                return items;

            AddItems(items, result.DisplayRewards);
            AddItems(items, result.DoubleRewards);
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
                });
            }
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
            writer.WriteInt32(0);
        }

        private static void WriteSingleRewardRow(GamePacketWriter writer, PackageGrantedItem reward)
        {
            WriteRewardRow(writer, reward);
            writer.WriteInt32(0);
        }

        private static void WriteRewardRow(GamePacketWriter writer, PackageGrantedItem reward)
        {
            writer.WriteInt16(-1);
            writer.WriteInt32(reward.ItemTemplateId);
            writer.WriteInt32(Math.Max(1, reward.DisplayCount));
            writer.WriteInt16(0);
            writer.WriteByte(0);
            writer.WriteInt32(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteInt16(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        private static ushort ToUInt16(int value)
        {
            if (value <= 0)
                return 0;
            if (value > ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }
    }
}
