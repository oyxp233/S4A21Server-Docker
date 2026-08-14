using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DfoServer.Game.Inventory
{
    internal readonly struct PetCreatureEvolutionQuestState
    {
        public PetCreatureEvolutionQuestState(
            int creatureId,
            int evolutionCreatureId,
            int evolutionItemTemplateId,
            int evolutionLevel)
        {
            CreatureId = creatureId;
            EvolutionCreatureId = evolutionCreatureId;
            EvolutionItemTemplateId = evolutionItemTemplateId;
            EvolutionLevel = evolutionLevel;
        }

        public int CreatureId { get; }
        public int EvolutionCreatureId { get; }
        public int EvolutionItemTemplateId { get; }
        public int EvolutionLevel { get; }
    }

    public readonly struct PetCreatureEvolutionResult
    {
        public PetCreatureEvolutionResult(
            bool changed,
            int creatureKey,
            int currentCreatureId,
            int evolvedCreatureId,
            int evolvedCreatureParam,
            int previousItemTemplateId,
            int evolvedItemTemplateId,
            short equipmentSlot)
        {
            Changed = changed;
            CreatureKey = creatureKey;
            CurrentCreatureId = currentCreatureId;
            EvolvedCreatureId = evolvedCreatureId;
            EvolvedCreatureParam = evolvedCreatureParam;
            PreviousItemTemplateId = previousItemTemplateId;
            EvolvedItemTemplateId = evolvedItemTemplateId;
            EquipmentSlot = equipmentSlot;
        }

        public bool Changed { get; }
        public int CreatureKey { get; }
        public int CurrentCreatureId { get; }
        public int EvolvedCreatureId { get; }
        public int EvolvedCreatureParam { get; }
        public int PreviousItemTemplateId { get; }
        public int EvolvedItemTemplateId { get; }
        public short EquipmentSlot { get; }

        public static PetCreatureEvolutionResult Noop { get; } =
            new PetCreatureEvolutionResult(false, 0, 0, 0, 0, 0, 0, 0);
    }

    internal sealed class PetCreatureEvolutionCatalog
    {
        private readonly Dictionary<int, PetCreatureEvolutionEntry> _byCreatureId;
        private readonly Dictionary<int, PetCreatureEvolutionEntry> _byItemId;

        private PetCreatureEvolutionCatalog(
            Dictionary<int, PetCreatureEvolutionEntry> byCreatureId,
            Dictionary<int, PetCreatureEvolutionEntry> byItemId)
        {
            _byCreatureId = byCreatureId;
            _byItemId = byItemId;
        }

        internal bool TryResolveByItemId(int itemTemplateId, out PetCreatureEvolutionEntry entry)
            => _byItemId.TryGetValue(itemTemplateId, out entry);

        internal bool TryResolveByCreatureId(int creatureId, out PetCreatureEvolutionEntry entry)
            => _byCreatureId.TryGetValue(creatureId, out entry);

        internal bool TryResolvePreviousByEvolutionItemId(int evolutionItemTemplateId, out PetCreatureEvolutionEntry entry)
        {
            foreach (var candidate in _byItemId.Values)
            {
                if (candidate.EvolutionItemTemplateId == evolutionItemTemplateId)
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = default(PetCreatureEvolutionEntry);
            return false;
        }

        internal static PetCreatureEvolutionCatalog Load()
        {
            var byCreatureId = new Dictionary<int, PetCreatureEvolutionEntry>();
            var byItemId = new Dictionary<int, PetCreatureEvolutionEntry>();

            try
            {
                var creatureList = LstFile.Parse(ReadPvfText("Creature/Creature.lst", "creature/creature.lst"));
                var creatureFiles = LoadCreatureFiles(creatureList);
                var equipmentFiles = LoadCreatureEquipmentFiles(creatureList);
                var itemByCreatureId = new Dictionary<int, int>();

                foreach (var equipment in equipmentFiles.Values)
                {
                    if (equipment.CreatureId > 0 && !itemByCreatureId.ContainsKey(equipment.CreatureId))
                        itemByCreatureId[equipment.CreatureId] = equipment.ItemTemplateId;
                }

                foreach (var equipment in equipmentFiles.Values)
                {
                    try
                    {
                        if (!creatureFiles.TryGetValue(equipment.CreatureId, out var creature))
                            continue;

                        var evolutionCreatureId = ParseInt(creature.EvolutionCreatureId);
                        var evolutionLevel = creature.EvolutionLevel > 0 ? creature.EvolutionLevel : 0;
                        var hasEvolutionQuest = HasEvolutionQuest(creature.EvolutionQuest);
                        var evolutionItemTemplateId = ResolveEvolutionItemTemplateId(
                            equipment,
                            evolutionCreatureId,
                            equipmentFiles,
                            itemByCreatureId);

                        var entry = new PetCreatureEvolutionEntry(
                            equipment.CreatureId,
                            equipment.ItemTemplateId,
                            equipment.CreatureParam,
                            evolutionCreatureId,
                            evolutionItemTemplateId,
                            evolutionLevel,
                            hasEvolutionQuest);
                        byCreatureId[equipment.CreatureId] = entry;
                        if (!byItemId.ContainsKey(equipment.ItemTemplateId))
                            byItemId[equipment.ItemTemplateId] = entry;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[PetCreatureEvolution] catalog entry skipped item=0x{equipment.ItemTemplateId:X8} creature={equipment.CreatureId}: {ex.Message}");
                    }
                }

                FileLogger.Log($"[PetCreatureEvolution] loaded creature entries={byCreatureId.Count} itemMappings={byItemId.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PetCreatureEvolution] catalog load failed: {ex.Message}");
            }

            return new PetCreatureEvolutionCatalog(byCreatureId, byItemId);
        }

        private static Dictionary<int, CreatureFile> LoadCreatureFiles(LstFile creatureList)
        {
            var result = new Dictionary<int, CreatureFile>();
            foreach (var entry in creatureList.Entries)
            {
                if (entry == null || entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                try
                {
                    var text = ReadPvfText(
                        Path.Combine("Creature", entry.FilePath),
                        Path.Combine("creature", entry.FilePath));
                    result[entry.Id] = CreatureFile.Parse(text);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetCreatureEvolution] creature file skipped creature={entry.Id} file={entry.FilePath}: {ex.Message}");
                }
            }

            return result;
        }

        private static Dictionary<int, PetCreatureEquipmentInfo> LoadCreatureEquipmentFiles(LstFile creatureList)
        {
            var result = new Dictionary<int, PetCreatureEquipmentInfo>();
            var creatureIdByFileName = BuildCreatureIdByFileName(creatureList);

            foreach (var equipment in ItemMetadataResolver.EquipmentList.Value.Entries)
            {
                if (equipment == null || equipment.Id <= 0 || string.IsNullOrWhiteSpace(equipment.FilePath))
                    continue;

                var normalizedPath = equipment.FilePath.Replace('\\', '/');
                if (!normalizedPath.StartsWith("creature/", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (!ItemMetadataResolver.TryLoadEquipmentFile(equipment.Id, out var file) || file == null)
                        continue;
                    if (!IsCreatureEquipment(file))
                        continue;

                    var creatureId = ResolveCreatureIdFromEquipmentPath(equipment.FilePath, creatureIdByFileName);
                    if (creatureId <= 0)
                        continue;

                    result[equipment.Id] = new PetCreatureEquipmentInfo(
                        equipment.Id,
                        creatureId,
                        file.OutputIndex,
                        ResolveCreatureParam(file, creatureId));
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetCreatureEvolution] creature equipment skipped item=0x{equipment.Id:X8} file={equipment.FilePath}: {ex.Message}");
                }
            }

            return result;
        }

        private static Dictionary<string, int> BuildCreatureIdByFileName(LstFile creatureList)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in creatureList.Entries)
            {
                if (entry == null || entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                var fileName = Path.GetFileNameWithoutExtension(entry.FilePath);
                if (!string.IsNullOrWhiteSpace(fileName) && !result.ContainsKey(fileName))
                    result[fileName] = entry.Id;
            }

            return result;
        }

        private static int ResolveCreatureIdFromEquipmentPath(
            string equipmentPath,
            Dictionary<string, int> creatureIdByFileName)
        {
            if (string.IsNullOrWhiteSpace(equipmentPath) || creatureIdByFileName == null)
                return 0;

            var fileName = Path.GetFileNameWithoutExtension(equipmentPath);
            return !string.IsNullOrWhiteSpace(fileName)
                && creatureIdByFileName.TryGetValue(fileName, out var creatureId)
                ? creatureId
                : 0;
        }

        private static int ResolveEvolutionItemTemplateId(
            PetCreatureEquipmentInfo equipment,
            int evolutionCreatureId,
            Dictionary<int, PetCreatureEquipmentInfo> equipmentFiles,
            Dictionary<int, int> itemByCreatureId)
        {
            if (equipment.OutputIndex > 0
                && equipment.OutputIndex != equipment.ItemTemplateId
                && equipmentFiles.ContainsKey(equipment.OutputIndex))
                return equipment.OutputIndex;

            if (evolutionCreatureId > 0
                && itemByCreatureId != null
                && itemByCreatureId.TryGetValue(evolutionCreatureId, out var itemTemplateId))
                return itemTemplateId;

            return 0;
        }

        private static string ReadPvfText(params string[] paths)
        {
            Exception last = null;
            foreach (var path in paths)
            {
                try
                {
                    return PvfArchiveAccessor.ReadText(path);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new FileNotFoundException("PVF creature script not found.");
        }

        private static int ParseInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Trim().Trim('`');
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static bool HasEvolutionQuest(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().Trim('`');
            if (value.Length == 0 || value == "0" || value == "-1")
                return false;

            return !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed > 0;
        }

        private static bool IsCreatureEquipment(EquipmentFile equipment)
        {
            var type = equipment?.EquipmentType;
            return !string.IsNullOrWhiteSpace(type)
                && type.IndexOf("[creature]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ResolveCreatureParam(EquipmentFile equipment, int creatureId)
        {
            return creatureId > 0 ? creatureId : 0;
        }
    }

    internal readonly struct PetCreatureEquipmentInfo
    {
        public PetCreatureEquipmentInfo(int itemTemplateId, int creatureId, int outputIndex, int creatureParam)
        {
            ItemTemplateId = itemTemplateId;
            CreatureId = creatureId;
            OutputIndex = outputIndex;
            CreatureParam = creatureParam;
        }

        public int ItemTemplateId { get; }
        public int CreatureId { get; }
        public int OutputIndex { get; }
        public int CreatureParam { get; }
    }

    internal readonly struct PetCreatureEvolutionEntry
    {
        public PetCreatureEvolutionEntry(
            int creatureId,
            int itemTemplateId,
            int creatureParam,
            int evolutionCreatureId,
            int evolutionItemTemplateId,
            int evolutionLevel,
            bool hasEvolutionQuest)
        {
            CreatureId = creatureId;
            ItemTemplateId = itemTemplateId;
            CreatureParam = creatureParam;
            EvolutionCreatureId = evolutionCreatureId;
            EvolutionItemTemplateId = evolutionItemTemplateId;
            EvolutionLevel = evolutionLevel;
            HasEvolutionQuest = hasEvolutionQuest;
        }

        public int CreatureId { get; }
        public int ItemTemplateId { get; }
        public int CreatureParam { get; }
        public int EvolutionCreatureId { get; }
        public int EvolutionItemTemplateId { get; }
        public int EvolutionLevel { get; }
        public bool HasEvolutionQuest { get; }
        public bool CanAutoEvolve => EvolutionLevel > 0 && EvolutionItemTemplateId > 0 && !HasEvolutionQuest;
    }
}
