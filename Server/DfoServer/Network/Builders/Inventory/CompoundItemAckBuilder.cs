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
                writer.WriteByte(core != null ? core.Attr : reward.Attr);
                writer.WriteUInt16(core != null ? core.Durability : reward.Durability);
                writer.WriteByte(core != null ? core.SealFlag : (byte)0);
                writer.WriteUInt16(core != null ? core.AmplifyValue : (ushort)0);
                writer.WriteByte(core != null ? core.AmplifyType : (byte)0);
                writer.WriteInt32(ResolveRewardMarker(core));
                writer.WriteByte(core != null ? core.GenuineUpgrade : (byte)0);
                writer.WriteByte(core != null ? core.EmancipateEquipmentLevel : (byte)0);
                writer.WriteByte(core != null ? core.TradeRestriction : (byte)0);
                writer.WriteUInt16(core != null ? core.TailUnknown0 : (ushort)0);
                writer.WriteByte(core != null ? core.TailUnknown1 : (byte)0);
                writer.WriteByte(core != null ? core.TailUnknown2 : (byte)0);
                writer.WriteByte(core != null ? core.TailUnknown3 : (byte)0);
                writer.WriteByte(core != null ? core.RemainUseCount : (byte)0);
                writer.WriteByte(core != null ? core.SortLockFlag : (byte)0);
                writer.WriteByte(core != null ? core.EquipmentLockId : (byte)0);
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

        private static int ResolveRewardMarker(ItemCore core)
        {
            if (core == null || core.Marker16 == ItemCore.Marker16Default)
                return 0;

            return core.Marker16;
        }
    }
}
