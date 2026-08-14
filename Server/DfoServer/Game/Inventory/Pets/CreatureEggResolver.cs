using PvfLib;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class CreatureEggResolver
    {
        internal static bool TryResolveHatchedCreatureItemId(int eggItemTemplateId, out int hatchedItemTemplateId)
        {
            hatchedItemTemplateId = 0;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(eggItemTemplateId, out EquipmentFile equipment)
                || equipment == null)
            {
                return false;
            }

            if (!IsCreatureEquipment(equipment))
                return false;

            if (equipment.OutputIndex <= 0 || equipment.OutputIndex == eggItemTemplateId)
                return false;

            hatchedItemTemplateId = equipment.OutputIndex;
            return true;
        }

        private static bool IsCreatureEquipment(EquipmentFile equipment)
        {
            var equipmentType = equipment?.EquipmentType;
            if (string.IsNullOrWhiteSpace(equipmentType))
                return false;

            return equipmentType.IndexOf("[creature]", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
