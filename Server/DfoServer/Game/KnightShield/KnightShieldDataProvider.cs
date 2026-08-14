using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.KnightShield
{
    public static class KnightShieldDataProvider
    {
        public const byte GuardianJob = 12;

        private const string ShieldWindowDataPath = "etc/character/knight/shieldwindowdata.etc";

        private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<int>>> CatalogItemsByGrowType =
            new Lazy<IReadOnlyDictionary<int, IReadOnlyList<int>>>(LoadCatalogItemsByGrowType);

        private static readonly ConcurrentDictionary<int, bool> ShieldValidationCache =
            new ConcurrentDictionary<int, bool>();

        public static bool IsEligibleCharacter(CharacterRecord character)
        {
            return character != null
                && IsEligibleCharacter(character.Job);
        }

        public static bool IsEligibleCharacter(byte job)
        {
            return job == GuardianJob;
        }

        public static int NormalizeGrowType(int growType)
        {
            return growType & 0x0F;
        }

        public static IReadOnlyList<int> GetCatalogItems(int growType)
        {
            return CatalogItemsByGrowType.Value.TryGetValue(
                NormalizeGrowType(growType),
                out var items)
                ? items
                : Array.Empty<int>();
        }

        public static bool IsCatalogShield(int growType, int itemId)
        {
            var items = GetCatalogItems(growType);
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] == itemId)
                    return IsKnightShield(itemId);
            }

            return false;
        }

        public static bool IsKnightShield(int itemId)
        {
            if (itemId <= 0)
                return false;

            return ShieldValidationCache.GetOrAdd(itemId, IsKnightShieldCore);
        }

        private static bool IsKnightShieldCore(int itemId)
        {
            try
            {
                if (!ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment))
                    return false;

                return EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType) == EquipmentType.SupportWeapon
                    && string.Equals(equipment.ItemGroupName?.Trim(), "shield", StringComparison.OrdinalIgnoreCase)
                    && ContainsKnightJob(equipment);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[KnightShield] PVF validation failed item={itemId}: {ex.Message}");
                return false;
            }
        }

        private static bool ContainsKnightJob(PvfLib.EquipmentFile equipment)
        {
            if (equipment?.Root == null)
                return false;

            foreach (var node in equipment.Root.GetChildren("usable job"))
            {
                foreach (var item in node.DataItems)
                {
                    var label = (item.GetContent(equipment.Content) ?? string.Empty)
                        .Trim()
                        .Trim('`')
                        .Trim();
                    if (label.Length >= 2 && label[0] == '[' && label[label.Length - 1] == ']')
                        label = label.Substring(1, label.Length - 2).Trim();
                    if (string.Equals(label, "knight", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static IReadOnlyDictionary<int, IReadOnlyList<int>> LoadCatalogItemsByGrowType()
        {
            var content = PvfArchiveAccessor.ReadText(ShieldWindowDataPath);
            var root = new ScriptParser().Parse(content);
            var mutable = new Dictionary<int, List<int>>();

            foreach (var tab in root.GetChildren("tab"))
            {
                foreach (var shield in tab.GetChildren("shield"))
                {
                    var itemId = ReadRequiredInt(shield, "item index", content);
                    var growType = NormalizeGrowType(ReadRequiredInt(shield, "grow type", content));
                    if (itemId <= 0 || growType <= 0)
                        throw new FormatException(
                            $"{ShieldWindowDataPath} contains invalid shield item={itemId} growType={growType}");

                    if (!mutable.TryGetValue(growType, out var items))
                    {
                        items = new List<int>();
                        mutable.Add(growType, items);
                    }
                    if (items.Contains(itemId))
                        throw new FormatException(
                            $"{ShieldWindowDataPath} duplicates shield item={itemId} growType={growType}");
                    items.Add(itemId);
                }
            }

            var result = new Dictionary<int, IReadOnlyList<int>>();
            foreach (var pair in mutable)
                result.Add(pair.Key, pair.Value.AsReadOnly());
            return new ReadOnlyDictionary<int, IReadOnlyList<int>>(result);
        }

        private static int ReadRequiredInt(ScriptNode parent, string tag, string content)
        {
            foreach (var node in parent.GetChildren(tag))
            {
                if (node.DataItems.Count > 0
                    && int.TryParse(node.GetFirstDataContent(content).Trim(), out var value))
                    return value;
            }

            throw new FormatException($"{ShieldWindowDataPath} shield entry is missing [{tag}]");
        }
    }
}
