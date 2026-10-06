using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class CompoundItemAckBuilder
    {
        private const int A21CompoundAckReservedTailSize = 12;

        public static byte[] Build(CompoundItemRecipeResult result)
        {
            if (result == null || !result.Success)
                return BuildError(result != null && result.ErrorCode != 0 ? result.ErrorCode : (byte)17);

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            WriteDeletedEntries(writer, result.DeletedEntries);
            WriteRewardEntries(writer, result.Rewards);
            writer.WriteZeroBytes(A21CompoundAckReservedTailSize);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            return new[] { (byte)0, errorCode };
        }

        private static void WriteDeletedEntries(GamePacketWriter writer, IReadOnlyList<CompoundItemDeletedEntry> entries)
        {
            var count = entries != null ? Math.Min(entries.Count, byte.MaxValue) : 0;
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                var entry = entries[index];
                writer.WriteByte(ResolveChangedEntryKind(entry));
                writer.WriteInt16(entry.SlotIndex);
                writer.WriteInt32(ResolveChangedEntryValue(entry));
            }
        }

        private static void WriteRewardEntries(GamePacketWriter writer, IReadOnlyList<BoosterRewardResult> rewards)
        {
            var count = rewards != null ? Math.Min(rewards.Count, byte.MaxValue) : 0;
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                var reward = rewards[index];
                var core = reward.CoreSnapshot;
                writer.WriteByte(ResolveRewardKind(reward, core));
                writer.WriteInt16(reward.SlotIndex);
                writer.WriteInt32(reward.ItemTemplateId);
                writer.WriteInt32(ResolveRewardValue(reward, core));
                // 强化/增幅/锻造等字段不写入 ACK：客户端在保留位读到非 0 值会卡死，
                // 物品真实属性由随后的 UPDATE_ITEM_LIST 通知下发。
                writer.WriteZeroBytes(22);
            }
        }

        private static byte ResolveChangedEntryKind(CompoundItemDeletedEntry entry)
        {
            if (entry?.SourceSnapshot != null
                && entry.RemainingCount <= 0
                && !InventoryStackRuleService.IsStackable(entry.SourceSnapshot))
            {
                return 1;
            }

            return entry != null ? (byte)entry.ListType : (byte)0;
        }

        private static int ResolveChangedEntryValue(CompoundItemDeletedEntry entry)
        {
            if (entry == null)
                return 0;

            return entry.RemainingCount > 0
                ? entry.RemainingCount
                : Math.Max(1, entry.Count);
        }

        private static byte ResolveRewardKind(BoosterRewardResult reward, ItemCore core)
        {
            if (core != null && core.ItemKind == ItemCore.KindEquipment)
                return ItemCore.KindEquipment;

            return reward != null ? (byte)reward.ListType : (byte)0;
        }

        private static int ResolveRewardValue(BoosterRewardResult reward, ItemCore core)
        {
            if (core != null && !InventoryStackRuleService.IsStackable(core))
                return core.Value;

            if (reward == null)
                return 1;

            return Math.Max(1, reward.GrantedCount);
        }
    }
}
