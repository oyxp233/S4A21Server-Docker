using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Inventory
{
    internal sealed class EquipmentDefinition
    {
        public int ItemTemplateId { get; set; }

        public string FilePath { get; set; }

        public int Rarity { get; set; }

        public int Grade { get; set; }

        public int CreationRate { get; set; }

        public int MinimumLevel { get; set; }

        public string ItemGroupName { get; set; }

        public string ItemCategory { get; set; }

        public string EquipmentType { get; set; }

        public string AttachType { get; set; }

        public int[] ForceResultItemRule { get; set; }

        public bool Legacy { get; set; }
    }

    internal static class EquipmentDefinitionCatalog
    {
        private static readonly Lazy<IReadOnlyList<EquipmentDefinition>> Definitions
            = new Lazy<IReadOnlyList<EquipmentDefinition>>(Load);

        internal static IReadOnlyList<EquipmentDefinition> GetAll()
            => Definitions.Value;

        internal static void Warmup()
            => _ = Definitions.Value;

        private static IReadOnlyList<EquipmentDefinition> Load()
        {
            var result = new List<EquipmentDefinition>();
            var errors = 0;
            foreach (var entry in ItemMetadataResolver.EquipmentList.Value.Entries)
            {
                if (entry == null || entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                try
                {
                    var equipment = EquipmentFile.Parse(PvfArchiveAccessor.ReadText(
                        Path.Combine("equipment", entry.FilePath)));
                    if (equipment == null)
                        continue;

                    result.Add(new EquipmentDefinition
                    {
                        ItemTemplateId = entry.Id,
                        FilePath = entry.FilePath,
                        Rarity = equipment.Rarity,
                        Grade = equipment.Grade,
                        CreationRate = equipment.CreationRate,
                        MinimumLevel = equipment.MinimumLevel,
                        ItemGroupName = equipment.ItemGroupName,
                        ItemCategory = equipment.ItemCategory,
                        EquipmentType = equipment.EquipmentType,
                        AttachType = equipment.AttachType,
                        ForceResultItemRule = equipment.ForceResultItemRule,
                        Legacy = string.Equals(
                            equipment.ItemCategory?.Trim(),
                            "legacy",
                            StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch
                {
                    errors++;
                }
            }

            FileLogger.Log($"[EquipmentDefinitionCatalog] loaded={result.Count} errors={errors}");
            return result.AsReadOnly();
        }
    }
}
