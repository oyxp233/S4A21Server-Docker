using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld.Auction;

namespace DfoServer.Game.Auction.Queries
{
    // Native node IDs/labels are evidence; membership below is the explicit
    // reversible product policy in M4-CONTRACT, not the original server formula.
    internal sealed class AuctionNativeCategoryResolver
    {
        internal const string PolicyVersion = "private-native-category-20261023-v1";
        private const string Evidence = "M4-CONTRACT.md Private category inclusion policy; "
            + "m2/protocol/SEARCH-EVIDENCE.md Item classification tree; search-category-map.json; "
            + "m0/catalog/itemtypemaster.etc.txt weapon/armor/item rows; "
            + ".scratch/weapon-illusion-productization/weapon-type-inputs/types.txt; "
            + "EquipmentTypeInfo and exact AuctionTemplateCatalog projection; " + PolicyVersion;
        private readonly AuctionTemplateCatalog catalog;
        private readonly Dictionary<ushort, Func<AuctionTemplateProjection, AuctionQueryMatch>> predicates = new();
        private static readonly string[][] WeaponJobs = {
            new[] { "swordman", "at swordman", "demonic swordman", "knight" },
            new[] { "fighter", "at fighter" }, new[] { "gunner", "at gunner" },
            new[] { "mage", "at mage", "creator mage" }, new[] { "priest" },
            new[] { "thief" }, new[] { "demonic lancer" }
        };
        private static readonly HashSet<string> KnownJobs = new(WeaponJobs.SelectMany(x => x), StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> StackFamilies = BuildStackFamilies();
        private static readonly HashSet<EquipmentType> ArmorTypes = new() {
            EquipmentType.Coat, EquipmentType.Shoulder, EquipmentType.Pants, EquipmentType.Shoes, EquipmentType.Waist };

        internal AuctionNativeCategoryResolver(AuctionTemplateCatalog catalog)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Equipment(10000, type => type == EquipmentType.Weapon);
            var parents = new ushort[] { 10101, 10201, 10301, 10401, 10501, 10601, 10801 };
            int[][] subtypes = { new[] { 0, 1, 2, 3, 5 }, new[] { 0, 1, 2, 3, 5 },
                new[] { 0, 1, 2, 3, 4 }, new[] { 0, 1, 2, 3, 4 }, new[] { 0, 1, 2, 3, 4 },
                new[] { 0, 1, 3, 5 }, new[] { 0, 1 } };
            for (int family = 0; family < parents.Length; family++)
            {
                int capturedFamily = family;
                predicates.Add(parents[family], template => Weapon(template, capturedFamily, null, subtypes[capturedFamily]));
                for (int leaf = 0; leaf < subtypes[family].Length; leaf++)
                {
                    int subtype = subtypes[family][leaf];
                    predicates.Add((ushort)(parents[family] + leaf + 1), template => Weapon(template, capturedFamily, subtype, subtypes[capturedFamily]));
                }
            }
            Equipment(11000, type => ArmorTypes.Contains(type));
            var armorRoots = new ushort[] { 11001, 11100, 11200, 11300, 11400 };
            var armorSlots = new[] { EquipmentType.Coat, EquipmentType.Shoulder, EquipmentType.Pants, EquipmentType.Shoes, EquipmentType.Waist };
            for (int material = 0; material < armorRoots.Length; material++)
            {
                int capturedMaterial = material;
                predicates.Add(armorRoots[material], t => Armor(t, capturedMaterial, null));
                for (int slot = 0; slot < armorSlots.Length; slot++)
                {
                    var capturedSlot = armorSlots[slot];
                    predicates.Add((ushort)(armorRoots[material] + slot + 1), t => Armor(t, capturedMaterial, capturedSlot));
                }
            }
            Equipment(12000, t => EquipmentTypeInfo.IsAccessory(t) || t == EquipmentType.TitleName);
            Equipment(12001, t => t == EquipmentType.Amulet);
            Equipment(12002, t => t == EquipmentType.Ring);
            Equipment(12003, t => t == EquipmentType.Wrist);
            Equipment(12004, t => t == EquipmentType.TitleName);
            Equipment(32000, EquipmentTypeInfo.IsSpecialEquipment);
            Equipment(32001, t => t == EquipmentType.Support);
            Equipment(32100, t => t == EquipmentType.MagicStone);
            AddJobLeaves(EquipmentType.Support, new ushort[] { 32002, 32003, 32004, 32005, 32006, 32007, 32008,
                32009, 32010, 32011, 32012, 32013, 32014, 32015 });
            AddJobLeaves(EquipmentType.MagicStone, new ushort[] { 32101, 32102, 32103, 32104, 32105, 32106, 32107,
                32108, 32109, 32110, 32111, 32112, 32113, 32114 });
            predicates.Add(13000, t => Stack(t, family => family >= 1 && family <= 6));
            for (int family = 1; family <= 6; family++)
            {
                int capturedFamily = family;
                predicates.Add((ushort)(13000 + family), t => Stack(t, actual => actual == capturedFamily));
            }
            predicates.Add(31000, t => Stack(t, family => family == 7));
            predicates.Add(33004, t => Stack(t, _ => Token(t.StackableType) == "material expert job"));
            predicates.Add(14000, t => t.Kind == "stackable" ? Stack(t, family => family == 8)
                : EquipmentMatch(t, type => type == EquipmentType.Creature || type is EquipmentType.ArtifactRed or EquipmentType.ArtifactBlue or EquipmentType.ArtifactGreen));
            Equipment(14001, t => t == EquipmentType.ArtifactRed);
            Equipment(14002, t => t == EquipmentType.ArtifactBlue);
            Equipment(14003, t => t == EquipmentType.ArtifactGreen);
            // Recipe children need output semantics; profession/other need
            // additional classification metadata. No fallback to "other".
        }

        internal AuctionQueryCategoryRule Resolve(ushort nativeCategory)
        {
            if (!predicates.TryGetValue(nativeCategory, out var predicate)) return null;
            return new(nativeCategory, catalog.SourcePvfSha256, Evidence, template => {
                if (template == null || template.SourcePvfSha256 != catalog.SourcePvfSha256
                    || !catalog.TryGet(template.TemplateId, out var exact) || !ReferenceEquals(template, exact))
                    return AuctionQueryMatch.Unknown;
                return predicate(template);
            });
        }

        private void Equipment(ushort code, Func<EquipmentType, bool> matches)
            => predicates.Add(code, template => EquipmentMatch(template, matches));
        private static AuctionQueryMatch EquipmentMatch(AuctionTemplateProjection t, Func<EquipmentType, bool> matches)
        {
            if (t.Kind == "stackable") return AuctionQueryMatch.NoMatch;
            if (t.Kind != "equipment" || !EquipmentTypeInfo.TryParse(t.EquipmentType, out var type)) return AuctionQueryMatch.Unknown;
            return Match(matches(type));
        }

        private static AuctionQueryMatch Armor(AuctionTemplateProjection t, int material, EquipmentType? slot)
        {
            var typeMatch = EquipmentMatch(t, type => ArmorTypes.Contains(type) && (!slot.HasValue || type == slot.Value));
            if (typeMatch != AuctionQueryMatch.Match) return typeMatch;
            if (t.SubType < 0 || t.SubType > 4) return AuctionQueryMatch.Unknown;
            return Match(t.SubType == material);
        }

        private static AuctionQueryMatch Weapon(AuctionTemplateProjection t, int family, int? subtype, int[] knownSubtypes)
        {
            var typeMatch = EquipmentMatch(t, type => type == EquipmentType.Weapon);
            if (typeMatch != AuctionQueryMatch.Match) return typeMatch;
            var job = Job(t, WeaponJobs[family], !subtype.HasValue);
            if (job != AuctionQueryMatch.Match || !subtype.HasValue) return job;
            if (!knownSubtypes.Contains(t.SubType)) return AuctionQueryMatch.Unknown;
            return Match(t.SubType == subtype.Value);
        }

        private void AddJobLeaves(EquipmentType expected, ushort[] codes)
        {
            // Exact order follows the proved native labels, not character IDs.
            var jobs = new[] { "swordman", "at swordman", "demonic swordman", "fighter", "gunner", "mage", "creator mage",
                "at mage", "priest", "at gunner", "thief", "at fighter", "knight", "demonic lancer" };
            for (int index = 0; index < codes.Length; index++)
            {
                string job = jobs[index];
                predicates.Add(codes[index], t => {
                    var typeMatch = EquipmentMatch(t, type => type == expected);
                    return typeMatch == AuctionQueryMatch.Match ? Job(t, new[] { job }, true) : typeMatch;
                });
            }
        }

        private static AuctionQueryMatch Job(AuctionTemplateProjection t, string[] requested, bool universalAllowed)
        {
            if (t.UsableJobLabels.Count == 0) return AuctionQueryMatch.Unknown;
            bool unknown = false;
            foreach (var raw in t.UsableJobLabels)
            {
                string job = raw.Trim().ToLowerInvariant();
                if (job == "all")
                {
                    if (universalAllowed) return AuctionQueryMatch.Match;
                    unknown = true;
                }
                else if (requested.Contains(job, StringComparer.OrdinalIgnoreCase)) return AuctionQueryMatch.Match;
                else if (!KnownJobs.Contains(job)) unknown = true;
            }
            return unknown ? AuctionQueryMatch.Unknown : AuctionQueryMatch.NoMatch;
        }

        private static AuctionQueryMatch Stack(AuctionTemplateProjection t, Func<int, bool> matches)
        {
            if (t.Kind == "equipment") return AuctionQueryMatch.NoMatch;
            if (t.Kind != "stackable" || !StackFamilies.TryGetValue(Token(t.StackableType), out var family)) return AuctionQueryMatch.Unknown;
            return Match(matches(family));
        }
        private static string Token(string raw)
        {
            var match = Regex.Match(raw ?? "", @"^\s*`?\[(?<token>[a-z ]+)\]`?(?:\s+-?\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["token"].Value.ToLowerInvariant() : "";
        }
        private static AuctionQueryMatch Match(bool yes) => yes ? AuctionQueryMatch.Match : AuctionQueryMatch.NoMatch;
        private static Dictionary<string, int> BuildStackFamilies()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Add(int family, params string[] tokens) { foreach (var token in tokens) result.Add(token, family); }
            Add(1, "waste", "unlimited waste", "disguise", "global effect", "town and dungeon", "unlimited town and dungeon", "expert town potion", "upgrade limit cube");
            Add(2, "material", "material expert job");
            Add(3, "throwset", "throw", "set");
            Add(4, "legacy", "upgradable legacy", "stackable legacy", "multi upgradable legacy");
            Add(5, "quest", "quest receive");
            Add(6, "etc", "booster", "cera booster", "package", "cera package", "unlimited etc", "booster random", "contract", "enchant waste", "gem", "avatar emblem", "booster selection");
            Add(7, "recipe"); Add(8, "creature", "stackable creature", "feed");
            return result;
        }
    }
}
