using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.ItemUpgrade;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    public sealed class ChronicleRefineCommand
    {
        public short MaterialSlotIndex { get; set; }
        public int MaterialItemTemplateId { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public byte OptionNo { get; set; }
        public byte CharacterJob { get; set; }
        public byte FirstGrowType { get; set; }
    }

    public sealed class ChronicleRefineResult
    {
        public const byte ErrorInvalidMaterial = 0x04;
        public const byte ErrorInvalidTarget = 0x13;
        public const byte ErrorDurability = 0x07;
        public const byte ErrorDeleteFailed = 0x11;
        public const byte ErrorInventoryFull = 0x15;
        public const byte ErrorTemplateMismatch = 0x17;
        public const byte ErrorUnsupported = 0x13;
        public const byte ErrorOptionFull = 0x13;
        public const byte ErrorLocked = 0xD5;
        public const byte ErrorUnidentified = 0xAE;

        public bool Success { get; set; }
        public bool RefineSucceeded { get; set; }
        public bool TargetDestroyed { get; set; }
        public byte ErrorCode { get; set; }
        public ChronicleRefineCommand Command { get; set; }
        public int MaterialRemainingStackCount { get; set; }
        public byte EquipmentType { get; set; }
        public byte OptionCount { get; set; }
        public int SuccessProbability { get; set; }
        public int ProbabilityRoll { get; set; }
        public List<DisjointMaterialResult> FailureRewards { get; } = new List<DisjointMaterialResult>();

        public static ChronicleRefineResult Error(ChronicleRefineCommand command, byte errorCode)
        {
            return new ChronicleRefineResult { Command = command, ErrorCode = errorCode };
        }
    }

    internal static class ChronicleRefineProbability
    {
        public static bool IsSuccess(int probabilityPercent, int roll)
        {
            return probabilityPercent >= 100
                || (probabilityPercent >= 0 && roll >= 0 && roll <= probabilityPercent);
        }
    }

    internal static class ChronicleRefineJobMatcher
    {
        private static readonly string[] JobLabels =
        {
            "swordman", "fighter", "gunner", "mage", "priest",
            "at gunner", "thief", "at fighter", "at mage",
            "demonic swordman", "creator mage", "at swordman", "knight",
        };
        private static readonly Regex JobTokenPattern
            = new Regex(@"\[\s*(?<job>[^\]]+?)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool Matches(string rawJobs, byte characterJob)
        {
            if (string.IsNullOrWhiteSpace(rawJobs) || characterJob >= JobLabels.Length)
                return false;

            var expected = JobLabels[characterJob];
            var matches = JobTokenPattern.Matches(rawJobs);
            foreach (Match match in matches)
            {
                var job = match.Groups["job"].Value.Trim();
                if (string.Equals(job, "all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(job, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (matches.Count > 0)
                return false;

            var fallback = rawJobs.Trim().Trim('`').Trim().Trim('[', ']').Trim();
            return string.Equals(fallback, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fallback, expected, StringComparison.OrdinalIgnoreCase);
        }
    }


    internal static class ChronicleRefineMaterialResolver
    {
        private const int AuraTypeCount = 3;
        // The 86 client equipment-option protocol identifies red/blue/green aura
        // types with this contiguous base range. This is a protocol constant,
        // not a whitelist of consumable material item IDs.
        private const int PacketBaseAuraItemId = 1254;
        private const int SacredAuraItemId = 10090758;
        private static readonly ConcurrentDictionary<int, int> AuraTypesByItemId
            = new ConcurrentDictionary<int, int>();
        private static readonly ConcurrentDictionary<int, Lazy<StackableItemFile>> MaterialDefinitions
            = new ConcurrentDictionary<int, Lazy<StackableItemFile>>();

        public static bool IsRefineMaterial(StackableItemFile stackable)
        {
            return stackable?.ThreeChronicleEnchant != null
                && stackable.Type >= 0
                && stackable.Type < AuraTypeCount
                && stackable.ThreeChronicleEnchant.Probabilities.Count > 0
                && stackable.ThreeChronicleEnchant.Checks.Count > 0
                && stackable.ThreeChronicleEnchant.Skills.Count > 0;
        }

        public static void Warmup()
        {
            for (var auraType = 0; auraType < AuraTypeCount; auraType++)
            {
                TryResolveMaterial(PacketBaseAuraItemId + auraType, out _);
                TryResolveMaterial(SacredAuraItemId + auraType, out _);
            }
        }

        public static bool TryGetPacketAuraItemId(int auraType, out int itemTemplateId)
        {
            if (auraType >= 0 && auraType < AuraTypeCount)
            {
                itemTemplateId = PacketBaseAuraItemId + auraType;
                AuraTypesByItemId.TryAdd(itemTemplateId, auraType);
                return true;
            }

            itemTemplateId = 0;
            return false;
        }

        public static bool TryResolveMaterial(int itemTemplateId, out StackableItemFile stackable)
        {
            try
            {
                stackable = MaterialDefinitions.GetOrAdd(
                    itemTemplateId,
                    id => new Lazy<StackableItemFile>(() => LoadMaterialDefinition(id))).Value;
            }
            catch
            {
                stackable = null;
            }

            if (!IsRefineMaterial(stackable))
                return false;

            AuraTypesByItemId[itemTemplateId] = stackable.Type;
            return true;
        }

        public static bool TryGetAuraType(int itemTemplateId, out byte auraType)
        {
            if (AuraTypesByItemId.TryGetValue(itemTemplateId, out var cachedType)
                && cachedType >= 0
                && cachedType < AuraTypeCount)
            {
                auraType = (byte)cachedType;
                return true;
            }

            if (TryResolveMaterial(itemTemplateId, out var stackable))
            {
                auraType = (byte)stackable.Type;
                return true;
            }

            auraType = 0;
            return false;
        }

        public static bool TryGetFragmentItemId(StackableItemFile stackable, out int itemTemplateId)
        {
            itemTemplateId = 0;
            if (string.IsNullOrWhiteSpace(stackable?.NeedMaterial))
                return false;

            var token = stackable.NeedMaterial
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return int.TryParse(token, out itemTemplateId) && itemTemplateId > 0;
        }

        private static StackableItemFile LoadMaterialDefinition(int itemTemplateId)
        {
            return ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out var stackable)
                && IsRefineMaterial(stackable)
                ? stackable
                : null;
        }
    }

    internal static class ChronicleRefineProtocol
    {
        public static byte[] NormalizeMiddleData(int targetItemTemplateId, byte[] middleData)
        {
            var targetType = EquipmentTypeInfo.ParseOrUnknown(
                ItemMetadataResolver.ResolveEquipmentType(targetItemTemplateId));
            return NormalizeMiddleData(targetType, middleData);
        }

        public static byte[] NormalizeMiddleData(EquipmentType targetType, byte[] middleData)
        {
            var options = MakeEquipListCodec.BuildChronicleOptions(middleData);
            if (options.Length == 0)
                return middleData;

            var changed = false;
            for (var i = 0; i < options.Length; i++)
            {
                byte auraType;
                if (!ChronicleRefineMaterialResolver.TryGetAuraType(options[i].OptionId, out auraType))
                {
                    // Compatibility with the initial implementation, which stored
                    // skillId in OptionId and auraType in EquipmentType.
                    if (options[i].EquipmentType > 3)
                        continue;
                    auraType = options[i].EquipmentType;
                }

                if (ChronicleRefineMaterialResolver.TryGetPacketAuraItemId(auraType, out var auraItemId)
                    && options[i].OptionId != auraItemId)
                {
                    options[i].OptionId = auraItemId;
                    changed = true;
                }
                if (EquipmentTypeInfo.IsUpgradeTargetType(targetType)
                    && options[i].EquipmentType != (byte)targetType)
                {
                    options[i].EquipmentType = (byte)targetType;
                    changed = true;
                }
            }

            return changed ? MakeEquipListCodec.BuildMiddleData1A(options) : middleData;
        }
    }
}
