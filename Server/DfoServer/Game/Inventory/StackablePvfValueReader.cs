using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class StackablePvfValueReader
    {
        internal static bool TryReadOptionalSingleValue(
            StackableItemFile stackable,
            string tag,
            out bool hasValue,
            out string value)
        {
            hasValue = false;
            value = null;
            if (stackable?.Root == null)
                return false;

            var nodes = stackable.Root.GetChildren(tag);
            if (nodes.Count == 0)
                return true;
            if (nodes.Count != 1
                || nodes[0].Children.Count != 0
                || nodes[0].DataItems.Count != 1)
            {
                return false;
            }

            value = nodes[0].DataItems[0]
                .GetContent(stackable.Content)
                .Trim()
                .Trim('`')
                .Trim();
            hasValue = value.Length > 0;
            return hasValue;
        }

        internal static bool TryReadOptionalNonNegativeInt(
            StackableItemFile stackable,
            string tag,
            out bool hasValue,
            out int value)
        {
            value = 0;
            if (!TryReadOptionalSingleValue(stackable, tag, out hasValue, out var raw))
                return false;
            if (!hasValue)
                return true;
            return int.TryParse(raw, out value) && value >= 0;
        }
    }
}
