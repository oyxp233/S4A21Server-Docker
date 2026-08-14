using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class RandomOptionProtocolWriter
    {
        internal static void WriteDynamic(GamePacketWriter writer, ItemCore core)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            var options = core.RandomOptions;
            var count = Math.Min(3, options.Count);
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                writer.WriteByte(options[index].Type);
                writer.WriteByte(options[index].Value1);
                writer.WriteByte(options[index].Value2);
            }

            if (count <= 0)
                return;

            var changedIndex = ResolveChangedIndex(core);
            writer.WriteByte(core.RandomOptionState);
            writer.WriteByte(changedIndex);
            if (changedIndex == ItemCore.RandomOptionChangedIndexDefault)
                return;

            writer.WriteByte(core.RandomOptionChangeState);
            writer.WriteByte(core.RandomOptionChange.Type);
            writer.WriteByte(core.RandomOptionChange.Value1);
            writer.WriteByte(core.RandomOptionChange.Value2);
        }

        private static byte ResolveChangedIndex(ItemCore core)
        {
            return HasExplicitDynamicTail(core)
                ? core.RandomOptionChangedIndex
                : ItemCore.RandomOptionChangedIndexDefault;
        }

        private static bool HasExplicitDynamicTail(ItemCore core)
        {
            return core.RandomOptionState != 0
                || core.RandomOptionChangedIndex != 0
                || core.RandomOptionChangeState != 0
                || core.RandomOptionChange.Type != 0
                || core.RandomOptionChange.Value1 != 0
                || core.RandomOptionChange.Value2 != 0;
        }
    }
}
