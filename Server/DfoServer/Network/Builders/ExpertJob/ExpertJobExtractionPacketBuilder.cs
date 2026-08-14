using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Builders.ExpertJob
{
    internal static class ExpertJobExtractionPacketBuilder
    {
        internal static byte[] BuildSuccess(ExpertJobExtractionResult result)
        {
            if (result == null || result.ErrorCode != 0)
                throw new ArgumentException("a successful extraction result is required", nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte((byte)result.TargetListType);
            writer.WriteInt16(result.TargetSlotIndex);
            writer.WriteByte((byte)Math.Min(byte.MaxValue, result.Materials.Count));
            for (var index = 0; index < result.Materials.Count && index < byte.MaxValue; index++)
            {
                var material = result.Materials[index];
                writer.WriteInt16(material.SlotIndex);
                writer.WriteInt32(material.ItemTemplateId);
                writer.WriteInt32(material.Count);
            }
            return writer.ToArray();
        }
    }
}
