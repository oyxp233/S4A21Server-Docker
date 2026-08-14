using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal static class ExpertJobEquipmentStateResolver
    {
        internal const int Normal = 0;
        internal const int UnidentifiedAmplify = 1;
        internal const int Chronicle = 2;

        internal static int Resolve(ItemCore item)
        {
            if (item == null)
                return Normal;
            if ((item.AmplifyType & 0x80) != 0)
                return UnidentifiedAmplify;
            return item.ChronicleOptionCount > 0 ? Chronicle : Normal;
        }
    }
}
