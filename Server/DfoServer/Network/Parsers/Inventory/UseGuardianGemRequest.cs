using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class UseGuardianGemRequest
    {
        public int EquippedMedalItemTemplateId { get; set; }

        public short MaterialSlotIndex { get; set; }

        public int GuardianGemItemTemplateId { get; set; }

        public byte SocketIndex { get; set; }

        public static bool TryParse(byte[] body, out UseGuardianGemRequest request)
        {
            request = null;
            if (body == null || body.Length != 11)
                return false;

            var equippedMedalItemTemplateId = BitConverter.ToUInt32(body, 0);
            var materialSlotIndex = BitConverter.ToUInt16(body, 4);
            var guardianGemItemTemplateId = BitConverter.ToUInt32(body, 6);
            var socketIndex = body[10];

            if (equippedMedalItemTemplateId == 0
                || guardianGemItemTemplateId == 0
                || equippedMedalItemTemplateId > int.MaxValue
                || guardianGemItemTemplateId > int.MaxValue
                || materialSlotIndex > short.MaxValue
                || socketIndex >= ItemCore.GuardianGemSlotCount)
            {
                return false;
            }

            request = new UseGuardianGemRequest
            {
                EquippedMedalItemTemplateId = (int)equippedMedalItemTemplateId,
                MaterialSlotIndex = (short)materialSlotIndex,
                GuardianGemItemTemplateId = (int)guardianGemItemTemplateId,
                SocketIndex = socketIndex,
            };
            return true;
        }

        public GuardianGemUseCommand ToCommand()
        {
            return new GuardianGemUseCommand
            {
                EquippedMedalItemTemplateId = EquippedMedalItemTemplateId,
                MaterialSlotIndex = MaterialSlotIndex,
                GuardianGemItemTemplateId = GuardianGemItemTemplateId,
                SocketIndex = SocketIndex,
            };
        }
    }
}
