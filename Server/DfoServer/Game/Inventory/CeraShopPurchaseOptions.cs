using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class CeraShopPurchaseOptions
    {
        public List<AvatarPackageChoice> AvatarChoices { get; } = new List<AvatarPackageChoice>();

        public List<CeraShopSelectionChoice> SelectionChoices { get; } = new List<CeraShopSelectionChoice>();

        public bool HasAny => AvatarChoices.Count > 0 || SelectionChoices.Count > 0;
    }

    internal sealed class CeraShopSelectionChoice
    {
        public int PackageItemTemplateId { get; set; }

        public ushort GroupIndex { get; set; }

        public ushort SelectionIndex { get; set; }
    }
}
