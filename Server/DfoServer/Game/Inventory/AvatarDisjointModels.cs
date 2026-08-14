using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed class AvatarDisjointRequest
    {
        public short SlotIndex { get; set; }
        public int ExpectedItemTemplateId { get; set; }
    }

    public sealed class AvatarDisjointResult
    {
        public const byte ErrorInvalidRequest = 0x13;
        public const byte ErrorInventoryFull = 0x04;

        public AvatarDisjointRequest Request { get; set; }
        public byte ErrorCode { get; set; }
        public int SourceItemTemplateId { get; set; }
        public List<DisjointMaterialResult> Materials { get; } = new List<DisjointMaterialResult>();
    }
}
