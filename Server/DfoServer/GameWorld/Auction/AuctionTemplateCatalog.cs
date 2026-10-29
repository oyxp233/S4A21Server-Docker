using PvfLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DfoServer.GameWorld.Auction
{
    public enum AuctionPackagableGate { Unavailable, Passed, Rejected }

    // A template projection is descriptive. Presence here never grants listing eligibility.
    public sealed class AuctionTemplateProjection
    {
        internal AuctionTemplateProjection(int id, string kind, string path, string hash,
            PvfModelBase model, string name, int level, int rarity, string equipmentType,
            int subtype, string stackableType, string category, string attachType, int tradeLimit,
            IEnumerable<string> impossibleContents)
        {
            TemplateId = id;
            Kind = kind;
            ResourcePath = path;
            SourcePvfSha256 = hash;
            DisplayName = name;
            MinimumLevel = level;
            Rarity = rarity;
            EquipmentType = equipmentType;
            SubType = subtype;
            StackableType = stackableType;
            ItemCategory = category;
            AttachType = attachType;
            TradeLimit = tradeLimit;
            ImpossibleContents = Array.AsReadOnly((impossibleContents ?? Array.Empty<string>()).ToArray());
            var jobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in model.Root.GetChildren("usable job"))
            foreach (var data in node.DataItems)
            foreach (Match match in Regex.Matches(data.GetContent(model.Content) ?? "", @"\[(?<job>[^\]]+)\]"))
                jobs.Add(match.Groups["job"].Value.Trim());
            UsableJobLabels = Array.AsReadOnly(jobs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
            HasRandomOptionDefinition = model.HasTag("random option");
            HasAuctionFree = model.HasTag("auction free");
            // Current A21 common model reset VA0295D677 initializes +1C to0;
            // the cash parser overwrites it only when the tag is present.
            // Evidence: m2/rules/NATIVE-ELIGIBILITY-EVIDENCE.md.
            CashInteger = model.GetIntValue("cash", 0);
            var nativeTags = ReadNativeListingTags(model);
            PackagableGate = nativeTags.Gate;
            PackagableTagCount = nativeTags.Count;
            TemporalTagsAbsent = nativeTags.TemporalTagsAbsent;
            SingleMaterialArgument = nativeTags.MaterialArgument;
            HasSingleNativeWeaponDeclaration = nativeTags.SingleWeapon;
            HasSingleNativeWaistAvatarZeroDeclaration = nativeTags.SingleWaistAvatarZero;
            HasSingleNativeFreeAttachDeclaration = nativeTags.SingleFreeAttach;
            HasUnambiguousTradeDeclarations = HasSingleDeclaration(model, "attach type", false)
                && HasSingleDeclaration(model, model is EquipmentFile ? "equipment type" : "stackable type", true);
        }

        public int TemplateId { get; }
        public string Kind { get; }
        public string ResourcePath { get; }
        public string SourcePvfSha256 { get; }
        public string DisplayName { get; }
        public int MinimumLevel { get; }
        public int Rarity { get; }
        public string EquipmentType { get; }
        public int SubType { get; }
        public string StackableType { get; }
        public string ItemCategory { get; }
        public string AttachType { get; }
        public int TradeLimit { get; }
        public IReadOnlyList<string> ImpossibleContents { get; }
        public IReadOnlyList<string> UsableJobLabels { get; }
        public bool HasRandomOptionDefinition { get; }
        // Native PVF model flag15. This bypasses only the premium/coupon gate;
        // it never overrides binding, locks, expiry or transfer eligibility.
        public bool HasAuctionFree { get; }
        public int CashInteger { get; }
        // An independent native refusal gate, not complete listing permission.
        // Evidence: protocol/M5-ELIGIBILITY-COMMON.md, model flag16 -> item+18C.
        public AuctionPackagableGate PackagableGate { get; }
        public int PackagableTagCount { get; }
        // Absence is the supported fact; a present value0 is not an alias for
        // absence. All raw occurrences, including nested ones, participate.
        public bool TemporalTagsAbsent { get; }
        // The integer following `[material]` in one complete raw stackable-type
        // declaration. This is not the unrelated [sub type] / SubType field.
        // Duplicate, nested or unsupported declarations have no value.
        public int? SingleMaterialArgument { get; }
        // M8 evidence-backed equipment subset. Strict raw declarations avoid
        // first/last-tag or malformed-parser fallback becoming native proof.
        public bool HasSingleNativeWeaponDeclaration { get; }
        // M9 finite native7 profile: exactly one top-level raw waist/argument0.
        public bool HasSingleNativeWaistAvatarZeroDeclaration { get; }
        public bool HasSingleNativeFreeAttachDeclaration { get; }
        public bool HasUnambiguousTradeDeclarations { get; }

        private static bool HasSingleDeclaration(PvfModelBase model, string tag, bool argument)
        {
            var nodes = new List<ScriptNode>();
            void Visit(ScriptNode node)
            {
                if (string.Equals(node.Tag?.Trim(), tag, StringComparison.OrdinalIgnoreCase)) nodes.Add(node);
                foreach (var child in node.Children) Visit(child);
            }
            foreach (var child in model.Root.Children) Visit(child);
            if (nodes.Count != 1) return false;
            var value = nodes[0];
            if (!model.Root.Children.Contains(value) || value.HasEndTag || value.Children.Count != 0 || value.DataItems.Count != 1)
                return false;
            var match = Regex.Match(value.DataItems[0].GetContent(model.Content).Trim(),
                argument ? @"\A`\[[^\]]+\]`[ \t]+(?<argument>[+-]?[0-9]+)\z" : @"\A`\[[^\]]+\]`\z");
            return match.Success && (!argument || int.TryParse(match.Groups["argument"].Value,
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _));
        }

        private static (AuctionPackagableGate Gate, int Count, bool TemporalTagsAbsent, int? MaterialArgument, bool SingleWeapon, bool SingleWaistAvatarZero, bool SingleFreeAttach) ReadNativeListingTags(PvfModelBase model)
        {
            var pending = new Stack<(ScriptNode Node, bool TopLevel)>();
            foreach (var child in model.Root.Children) pending.Push((child, true));
            int count = 0;
            int stackTypeCount = 0, equipmentTypeCount = 0, attachTypeCount = 0;
            bool singleWeapon = false, singleWaistAvatarZero = false, singleFreeAttach = false;
            int? materialArgument = null;
            bool malformed = false, zero = false, temporalAbsent = true;
            while (pending.Count > 0)
            {
                var (node, topLevel) = pending.Pop();
                foreach (var child in node.Children) pending.Push((child, false));
                if (string.Equals(node.Tag?.Trim(), "usable period", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(node.Tag?.Trim(), "expiration date", StringComparison.OrdinalIgnoreCase)
                    || (model is EquipmentFile && string.Equals(node.Tag?.Trim(), "usable period after unsealing",
                        StringComparison.OrdinalIgnoreCase))) temporalAbsent = false;
                if (model is EquipmentFile && string.Equals(node.Tag?.Trim(), "equipment type", StringComparison.OrdinalIgnoreCase))
                {
                    equipmentTypeCount++;
                    if (topLevel && node.Tag == "equipment type" && !node.HasEndTag
                        && node.Children.Count == 0 && node.DataItems.Count == 1)
                    {
                        var declaration = Regex.Match(node.DataItems[0].GetContent(model.Content).Trim(),
                            @"\A`\[weapon\]`[ \t]+(?<argument>[+-]?[0-9]+)\z");
                        singleWeapon = declaration.Success && int.TryParse(declaration.Groups["argument"].Value,
                            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
                        singleWaistAvatarZero = Regex.IsMatch(node.DataItems[0].GetContent(model.Content).Trim(),
                            @"\A`\[waist avatar\]`[ \t]+0\z");
                    }
                }
                if (model is EquipmentFile && string.Equals(node.Tag?.Trim(), "attach type", StringComparison.OrdinalIgnoreCase))
                {
                    attachTypeCount++;
                    singleFreeAttach = topLevel && node.Tag == "attach type" && !node.HasEndTag
                        && node.Children.Count == 0 && node.DataItems.Count == 1
                        && node.DataItems[0].GetContent(model.Content).Trim() == "`[free]`";
                }
                if (string.Equals(node.Tag?.Trim(), "stackable type", StringComparison.OrdinalIgnoreCase))
                {
                    stackTypeCount++;
                    if (topLevel && node.Tag == "stackable type" && !node.HasEndTag
                        && node.Children.Count == 0 && node.DataItems.Count == 1)
                    {
                        var tuple = Regex.Match(node.DataItems[0].GetContent(model.Content).Trim(),
                            @"\A`\[material\]`[ \t]+(?<argument>[+-]?[0-9]+)\z");
                        if (tuple.Success && int.TryParse(tuple.Groups["argument"].Value,
                            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int argument))
                            materialArgument = argument;
                    }
                }
                if (!string.Equals(node.Tag?.Trim(), "packagable", StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                // Preserve every occurrence. GetValue/GetChild would silently
                // hide a later zero. Unsupported nested/block/extra-value
                // syntax is never interpreted as an absent or allowed scalar.
                if (!topLevel || node.Tag != node.Tag.Trim() || node.HasEndTag || node.Children.Count != 0 || node.DataItems.Count != 1
                    || !int.TryParse(node.DataItems[0].GetContent(model.Content).Trim(),
                        NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                { malformed = true; continue; }
                if (value == 0) zero = true;
            }
            return (malformed ? AuctionPackagableGate.Unavailable
                : zero ? AuctionPackagableGate.Rejected : AuctionPackagableGate.Passed, count, temporalAbsent,
                stackTypeCount == 1 ? materialArgument : null,
                equipmentTypeCount == 1 && singleWeapon, equipmentTypeCount == 1 && singleWaistAvatarZero,
                attachTypeCount == 1 && singleFreeAttach);
        }
    }

    public sealed class AuctionTemplateCatalog
    {
        private readonly IReadOnlyDictionary<int, AuctionTemplateProjection> _templates;

        private AuctionTemplateCatalog(string hash, IReadOnlyDictionary<int, AuctionTemplateProjection> templates)
        {
            SourcePvfSha256 = hash;
            _templates = templates;
        }

        public string SourcePvfSha256 { get; }
        public IReadOnlyDictionary<int, AuctionTemplateProjection> Templates => _templates;
        public bool TryGet(int templateId, out AuctionTemplateProjection projection)
            => _templates.TryGetValue(templateId, out projection);

        // The composition root supplies the existing archive owner and its exact fingerprint.
        // Optional model readers reuse ItemMetadataResolver's caches for that same archive.
        public static AuctionTemplateCatalog Load(Func<string, string> readText, string sourcePvfSha256,
            Func<int, EquipmentFile> readEquipment = null,
            Func<int, StackableItemFile> readStackable = null)
        {
            if (readText == null) throw new ArgumentNullException(nameof(readText));
            var hash = ValidateFingerprint(sourcePvfSha256);
            var result = new Dictionary<int, AuctionTemplateProjection>();
            foreach (var entry in ReadReferences(readText))
                result.Add(entry.Id, ProjectEntry(entry, hash, readText, readEquipment, readStackable));
            return new AuctionTemplateCatalog(hash, new ReadOnlyDictionary<int, AuctionTemplateProjection>(result));
        }

        // Only the two index resources are consumed here. Models/projections
        // are resolved per requested id through the same supplied archive owner.
        // The catalog retains projections, never a second cache of PVF models.
        public static AuctionTemplateCatalog LoadIndexed(Func<string, string> readText, string sourcePvfSha256,
            Func<int, EquipmentFile> readEquipment = null,
            Func<int, StackableItemFile> readStackable = null)
        {
            if (readText == null) throw new ArgumentNullException(nameof(readText));
            var hash = ValidateFingerprint(sourcePvfSha256);
            var entries = new Dictionary<int, Lazy<AuctionTemplateProjection>>();
            foreach (var entry in ReadReferences(readText))
                entries.Add(entry.Id, new Lazy<AuctionTemplateProjection>(
                    () => ProjectEntry(entry, hash, readText, readEquipment, readStackable),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return new AuctionTemplateCatalog(hash, new IndexedProjectionDictionary(entries));
        }

        private sealed record TemplateReference(int Id, string Kind, string Path);

        // Iterator preserves Load's original eager read/parse/failure order;
        // LoadIndexed fully consumes it while leaving every model Lazy untouched.
        private static IEnumerable<TemplateReference> ReadReferences(Func<string, string> readText)
        {
            var ids = new HashSet<int>();
            foreach (var kind in new[] { "equipment", "stackable" })
            {
                var text = RequireText(readText, kind + "/" + kind + ".lst");
                // LstFile's legacy digit-only regex would reinterpret -1 as1.
                // Reject negative key literals before handing it the index.
                if (Regex.IsMatch(text, @"(?:\A|\s)-[0-9]+\s+`"))
                    throw new InvalidDataException("Negative auction template id in " + kind + " index.");
                var list = LstFile.Parse(text);
                foreach (var entry in list.Entries)
                {
                    // The resource index includes virtual-currency id0. Index
                    // membership describes a resource; it grants no item kind,
                    // owned Main slot or listing capability.
                    if (entry.Id < 0 || !ids.Add(entry.Id))
                        throw new InvalidDataException("Invalid or ambiguous auction template id: " + entry.Id);
                    var relativePath = (entry.FilePath ?? "").Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("/")
                        || relativePath.Contains(':') || relativePath.Split('/').Any(x => x == ".." || x == "."))
                        throw new InvalidDataException("Invalid auction template path: " + relativePath);
                    yield return new TemplateReference(entry.Id, kind, kind + "/" + relativePath);
                }
            }
        }

        private static AuctionTemplateProjection ProjectEntry(TemplateReference entry, string hash,
            Func<string, string> readText, Func<int, EquipmentFile> readEquipment,
            Func<int, StackableItemFile> readStackable)
        {
            if (entry.Kind == "equipment")
            {
                var item = readEquipment == null
                    ? EquipmentFile.Parse(RequireText(readText, entry.Path)) : readEquipment(entry.Id);
                RequireModel(item, entry.Path);
                return new AuctionTemplateProjection(entry.Id, entry.Kind, entry.Path, hash, item,
                    item.Name, item.MinimumLevel, item.Rarity, item.EquipmentType, item.SubType,
                    null, item.ItemCategory, item.AttachType,
                    item.GetIntValue("trade limit max"), item.ImpossibleContentItems);
            }
            else
            {
                var item = readStackable == null
                    ? StackableItemFile.Parse(RequireText(readText, entry.Path)) : readStackable(entry.Id);
                RequireModel(item, entry.Path);
                return new AuctionTemplateProjection(entry.Id, entry.Kind, entry.Path, hash, item,
                    item.Name, item.MinimumLevel, item.Rarity, null, -1,
                    item.StackableType, item.ItemCategory, item.AttachType,
                    item.TradeLimit, item.ImpossibleContentItems);
            }
        }

        private sealed class IndexedProjectionDictionary : IReadOnlyDictionary<int, AuctionTemplateProjection>
        {
            private readonly Dictionary<int, Lazy<AuctionTemplateProjection>> entries;
            internal IndexedProjectionDictionary(Dictionary<int, Lazy<AuctionTemplateProjection>> entries)
                => this.entries = entries;
            public int Count => entries.Count;
            public IEnumerable<int> Keys => entries.Keys;
            public IEnumerable<AuctionTemplateProjection> Values => EnumerateValues();
            public AuctionTemplateProjection this[int key] => entries[key].Value;
            public bool ContainsKey(int key) => entries.ContainsKey(key);
            public bool TryGetValue(int key, out AuctionTemplateProjection value)
            {
                value = null;
                if (!entries.TryGetValue(key, out var entry)) return false;
                // An invalid known model remains an exception, not "not found".
                value = entry.Value;
                return true;
            }
            private IEnumerable<AuctionTemplateProjection> EnumerateValues()
            {
                foreach (var entry in entries.Values) yield return entry.Value;
            }
            public IEnumerator<KeyValuePair<int, AuctionTemplateProjection>> GetEnumerator()
            {
                foreach (var entry in entries)
                    yield return new KeyValuePair<int, AuctionTemplateProjection>(entry.Key, entry.Value.Value);
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        internal static string ValidateFingerprint(string hash)
        {
            if (hash == null || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
                throw new ArgumentException("An exact PVF SHA256 is required.", nameof(hash));
            return hash.ToLowerInvariant();
        }

        private static string RequireText(Func<string, string> readText, string path)
        {
            var text = readText(path);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Missing auction catalog resource: " + path);
            return text;
        }

        private static void RequireModel(PvfModelBase item, string path)
        {
            if (item?.Root == null || item.Root.Children.Count == 0)
                throw new InvalidDataException("Unknown auction template resource: " + path);
        }
    }
}
