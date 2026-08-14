using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class CompoundItemAckBuilder
    {
        public static byte[] Build(CompoundItemRecipeResult result)
        {
            if (result == null || !result.Success)
                return BuildError(result != null && result.ErrorCode != 0 ? result.ErrorCode : (byte)17);

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            WriteDeletedEntries(writer, result.DeletedEntries);
            WriteRewardEntries(writer, result.Rewards);
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
                writer.WriteByte((byte)entry.ListType);
                writer.WriteInt16(entry.SlotIndex);
                writer.WriteInt32(Math.Max(1, entry.Count));
            }
        }

        private static void WriteRewardEntries(GamePacketWriter writer, IReadOnlyList<BoosterRewardResult> rewards)
        {
            var count = rewards != null ? Math.Min(rewards.Count, byte.MaxValue) : 0;
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                var reward = rewards[index];
                var stackCount = Math.Max(1, reward.GrantedCount);
                writer.WriteByte((byte)reward.ListType);
                writer.WriteInt16(reward.SlotIndex);
                writer.WriteInt32(reward.ItemTemplateId);
                writer.WriteInt32(stackCount);
                writer.WriteByte(0);
                writer.WriteUInt16(0);
                writer.WriteByte(0);
                writer.WriteUInt16(0);
                writer.WriteByte(0);
                writer.WriteBytes(BuildEmptyInvenItemPacketTail());
            }
        }

        private static byte[] BuildEmptyInvenItemPacketTail()
        {
            // Client CMD 0x0019 ACK handler (0x00CE7E80) reads 32B per reward entry.
            return new byte[14];
        }
    }
}
