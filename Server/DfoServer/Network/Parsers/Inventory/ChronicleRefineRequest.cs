using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class ChronicleRefineRequest
    {
        public short MaterialSlotIndex { get; set; }

        public int MaterialItemTemplateId { get; set; }

        public byte MaterialPadding { get; set; }

        public short TargetSlotIndex { get; set; }

        public int TargetItemTemplateId { get; set; }

        public byte OptionNo { get; set; }

        public static bool TryParse(byte[] body, out ChronicleRefineRequest request)
        {
            request = null;
            if (body == null || body.Length != 14)
                return false;

            request = new ChronicleRefineRequest
            {
                MaterialSlotIndex = BitConverter.ToInt16(body, 0),
                MaterialItemTemplateId = BitConverter.ToInt32(body, 2),
                MaterialPadding = body[6],
                TargetSlotIndex = BitConverter.ToInt16(body, 7),
                TargetItemTemplateId = BitConverter.ToInt32(body, 9),
                OptionNo = body[13],
            };

            return request.MaterialSlotIndex >= 0
                && request.MaterialItemTemplateId > 0
                && request.MaterialPadding == 0
                && request.TargetSlotIndex >= 0
                && request.TargetItemTemplateId > 0;
        }

        public ChronicleRefineCommand ToCommand(byte characterJob, byte growType)
        {
            return new ChronicleRefineCommand
            {
                MaterialSlotIndex = MaterialSlotIndex,
                MaterialItemTemplateId = MaterialItemTemplateId,
                TargetSlotIndex = TargetSlotIndex,
                TargetItemTemplateId = TargetItemTemplateId,
                OptionNo = OptionNo,
                CharacterJob = characterJob,
                FirstGrowType = (byte)(growType & 0x0F),
            };
        }
    }
}
