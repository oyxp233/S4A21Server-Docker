using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class AuctionIndexedCatalogSelfTest
    {
        private const string Fingerprint = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
        private const string EquipmentText = "[name]\n`weapon`\n[equipment type]\n`[weapon]` 0\n[sub type]\n5\n[usable job]\n`[swordman]` `[knight]`\n[/usable job]\n[auction free]\n[impossible contents]\n`gift`\n[/impossible contents]\n";
        private const string StackText = "[name]\n`material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n";

        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction indexed catalog: {name}"); if (ok) passed++; else failed++; }
            bool Throws<T>(Action action) where T : Exception
            { try { action(); return false; } catch (T) { return true; } }
            try
            {
                var resources = new Dictionary<string, string> {
                    ["equipment/equipment.lst"] = "101 `a.equ` 102 `b.equ`",
                    ["stackable/stackable.lst"] = "201 `a.stk`",
                    ["equipment/a.equ"] = EquipmentText,
                    ["equipment/b.equ"] = EquipmentText,
                    ["stackable/a.stk"] = StackText
                };
                int textReads = 0, equipmentReads = 0, stackReads = 0;
                string Read(string path) { textReads++; return resources.TryGetValue(path, out var value) ? value : null; }
                var model = EquipmentFile.Parse(EquipmentText);
                EquipmentFile Equipment(int id) { equipmentReads++; return id == 101 || id == 102 ? model : null; }
                StackableItemFile Stack(int id) { stackReads++; return id == 201 ? StackableItemFile.Parse(StackText) : null; }
                var catalog = AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint.ToUpperInvariant(), Equipment, Stack);
                Check("construction reads two indexes without model reader calls", textReads == 2 && equipmentReads == 0 && stackReads == 0);
                Check("hash remains normalized exact shared-source identity", catalog.SourcePvfSha256 == Fingerprint);
                Check("count is index count without model materialization", catalog.Templates.Count == 3 && equipmentReads == 0 && stackReads == 0);
                Check("keys are available without materialization", catalog.Templates.Keys.OrderBy(x => x).SequenceEqual(new[] { 101, 102, 201 }) && equipmentReads == 0 && stackReads == 0);
                Check("ContainsKey reports index existence without materialization", catalog.Templates.ContainsKey(101) && !catalog.Templates.ContainsKey(999) && equipmentReads == 0 && stackReads == 0);
                Check("unknown TryGet is false and does not invoke readers", !catalog.TryGet(999, out var missing) && missing == null && equipmentReads == 0 && stackReads == 0);
                Check("unknown indexer preserves dictionary exception semantics", Throws<KeyNotFoundException>(() => _ = catalog.Templates[999]) && equipmentReads == 0 && stackReads == 0);
                using (var cursor = catalog.Templates.GetEnumerator())
                {
                    Check("getting enumerator does not request its first model", equipmentReads == 0 && stackReads == 0);
                    bool moved = cursor.MoveNext();
                    Check("first MoveNext materializes only first projection", moved && cursor.Current.Key == 101 && equipmentReads == 1 && stackReads == 0);
                }
                Check("TryGet and indexer share the same immutable projection", catalog.TryGet(101, out var first)
                    && ReferenceEquals(first, catalog.Templates[101]) && equipmentReads == 1 && stackReads == 0 && textReads == 2);
                model.Name = "changed shared model";
                model.ImpossibleContentItems.Clear();
                Check("projection deeply copies shared model facts", first.DisplayName == "weapon" && first.SubType == 5
                    && first.UsableJobLabels.SequenceEqual(new[] { "knight", "swordman" }) && first.ImpossibleContents.Count == 1 && first.HasAuctionFree);
                var values = catalog.Templates.Values;
                using (var cursor = values.GetEnumerator())
                {
                    Check("Values acquisition is lazy", equipmentReads == 1 && stackReads == 0);
                    cursor.MoveNext();
                    Check("Values first projection reuses the already-read id", ReferenceEquals(cursor.Current, first) && equipmentReads == 1 && stackReads == 0);
                    cursor.MoveNext();
                    Check("Values second MoveNext only loads next requested id", cursor.Current.TemplateId == 102 && equipmentReads == 2 && stackReads == 0);
                }
                Check("stack lookup does not reread list or equipment", catalog.TryGet(201, out var material)
                    && material.Kind == "stackable" && stackReads == 1 && equipmentReads == 2 && textReads == 2);
                Check("complete enumeration reuses all materialized projections", catalog.Templates.Count() == 3
                    && catalog.Templates.Values.Count() == 3 && equipmentReads == 2 && stackReads == 1 && textReads == 2);
                Check("nongeneric enumeration has same lazy dictionary shape", ((IEnumerable)catalog.Templates).Cast<object>().Count() == 3 && equipmentReads == 2 && stackReads == 1);

                int rawReads = 0;
                string Raw(string path) { rawReads++; return resources.TryGetValue(path, out var value) ? value : null; }
                var raw = AuctionTemplateCatalog.LoadIndexed(Raw, Fingerprint);
                Check("text-only mode still reads just the index at construction", rawReads == 2);
                Check("text-only mode reads one body on first target access", raw.TryGet(201, out var rawStack) && rawStack.Kind == "stackable" && rawReads == 3);
                Check("text-only repeated target access never reparses", ReferenceEquals(rawStack, raw.Templates[201]) && rawReads == 3);

                int brokenCalls = 0;
                var broken = AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, _ => { brokenCalls++; return null; }, Stack);
                Check("index can describe a known yet invalid body without eager parsing", broken.Templates.Count == 3 && broken.Templates.ContainsKey(101) && brokenCalls == 0);
                Check("known invalid target raises instead of becoming unknown", Throws<InvalidDataException>(() => broken.TryGet(101, out _)) && brokenCalls == 1);
                Check("failed Lazy cannot repeatedly hammer the model owner", Throws<InvalidDataException>(() => _ = broken.Templates[101]) && brokenCalls == 1);
                Check("one target failure leaves unrelated target available", broken.TryGet(201, out _) && brokenCalls == 1);
                var emptyBody = AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, _ => EquipmentFile.Parse(""), Stack);
                Check("empty known model retains strict RequireModel failure", Throws<InvalidDataException>(() => emptyBody.TryGet(101, out _)));
                var absentText = AuctionTemplateCatalog.LoadIndexed(path => path.EndsWith(".lst", StringComparison.Ordinal) ? Read(path) : null, Fingerprint);
                Check("missing indexed resource fails at first demand", Throws<InvalidDataException>(() => absentText.TryGet(101, out _)));

                int concurrentCalls = 0;
                var parallel = AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, _ => {
                    Interlocked.Increment(ref concurrentCalls); return EquipmentFile.Parse(EquipmentText);
                }, Stack);
                var results = new AuctionTemplateProjection[32];
                Parallel.For(0, results.Length, index => results[index] = parallel.Templates[101]);
                Check("concurrent requests invoke the shared model owner only once per id", concurrentCalls == 1 && results.All(x => ReferenceEquals(x, results[0])));

                int badReads = 0;
                string BadRead(string path) { badReads++; return Read(path); }
                Check("invalid hash fails before even index IO", Throws<ArgumentException>(() => AuctionTemplateCatalog.LoadIndexed(BadRead, "bad")) && badReads == 0);
                Check("missing index fails construction", Throws<InvalidDataException>(() => AuctionTemplateCatalog.LoadIndexed(_ => null, Fingerprint)));
                Check("missing reader fails construction", Throws<ArgumentNullException>(() => AuctionTemplateCatalog.LoadIndexed(null, Fingerprint)));
                int invalidModels = 0;
                EquipmentFile InvalidModel(int _) { invalidModels++; return model; }
                resources["stackable/stackable.lst"] = "0 `a.stk` 1 `a.stk` 2 `a.stk` 201 `a.stk`";
                foreach (bool indexed in new[] { false, true })
                {
                    var zeroCatalog = indexed ? AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint) : AuctionTemplateCatalog.Load(Read, Fingerprint);
                    Check("virtual currency ids remain descriptive index members " + indexed,
                        zeroCatalog.Templates.Count == 6 && zeroCatalog.Templates.ContainsKey(0)
                        && zeroCatalog.TryGet(0, out var zero) && zero.Kind == "stackable" && zero.TemplateId == 0);
                    Check("zero registry id cannot use ordinary instance kind resolver " + indexed,
                        !ItemMetadataResolver.TryResolveItemKind(0, out var kind) && kind == ItemCore.KindUnknown);
                    var envelope = AuctionItemEnvelope.Create(new ItemCore { ItemId = 0, ItemKind = ItemCore.KindMaterial, Count = 1 }.ToBytes(), "{}", "{}");
                    var native = AuctionNativeListingRuleResolver.Resolve(new(1, 1, zeroCatalog.Templates[0], envelope,
                        envelope.ContentHashHex, 1, 0, 86400, true));
                    Check("zero catalog id never becomes native listing permission " + indexed,
                        native.Kind == AuctionNativeProofKind.Unknown && native.Status == AuctionListingEligibilityStatus.Unavailable
                        && native.Capability == AuctionListingTransferCapability.None);
                }
                resources["stackable/stackable.lst"] = "201 `a.stk`";
                resources["equipment/equipment.lst"] = "-1 `a.equ`";
                Check("negative template id fails before model loading", Throws<InvalidDataException>(() => AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, InvalidModel, Stack)) && invalidModels == 0);
                resources["equipment/equipment.lst"] = "101 `a.equ`";
                resources["stackable/stackable.lst"] = "0 `a.stk` 0 `a.stk`";
                Check("duplicate zero id remains ambiguous", Throws<InvalidDataException>(() => AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, InvalidModel, Stack)) && invalidModels == 0);
                resources["stackable/stackable.lst"] = "201 `a.stk`";
                resources["equipment/equipment.lst"] = "101 `a.equ` 101 `b.equ`";
                Check("duplicate inside one list fails before all model loading", Throws<InvalidDataException>(() => AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, InvalidModel, Stack)) && invalidModels == 0);
                resources["equipment/equipment.lst"] = "101 `a.equ`";
                resources["stackable/stackable.lst"] = "101 `a.stk`";
                Check("cross-list ambiguous ownership fails before all model loading", Throws<InvalidDataException>(() => AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, InvalidModel, Stack)) && invalidModels == 0);
                resources["stackable/stackable.lst"] = "201 `a.stk`";
                foreach (string path in new[] { "../escape.equ", "/absolute.equ", "C:/outside.equ", "folder/./a.equ", "folder\\..\\outside.equ" })
                {
                    resources["equipment/equipment.lst"] = "101 `" + path + "`";
                    Check("index rejects path boundary " + path, Throws<InvalidDataException>(() => AuctionTemplateCatalog.LoadIndexed(Read, Fingerprint, InvalidModel, Stack)) && invalidModels == 0);
                }
                resources["equipment/equipment.lst"] = "101 `a.equ` 102 `b.equ`";
                int eagerEquipment = 0, eagerStack = 0;
                var eager = AuctionTemplateCatalog.Load(Read, Fingerprint,
                    _ => { eagerEquipment++; return EquipmentFile.Parse(EquipmentText); },
                    _ => { eagerStack++; return StackableItemFile.Parse(StackText); });
                Check("existing Load retains eager model semantics", eagerEquipment == 2 && eagerStack == 1 && eager.Templates.Count == 3);
                Check("existing Load still rejects known bad models during construction", Throws<InvalidDataException>(() => AuctionTemplateCatalog.Load(Read, Fingerprint, _ => null, Stack)));
                int eagerOrderCalls = 0;
                resources["stackable/stackable.lst"] = "101 `a.stk`";
                Check("existing eager first-list read order is preserved", Throws<InvalidDataException>(() => AuctionTemplateCatalog.Load(Read, Fingerprint,
                    _ => { eagerOrderCalls++; return EquipmentFile.Parse(EquipmentText); }, Stack)) && eagerOrderCalls == 2);
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected auction indexed catalog exception: " + ex); failed++; }
            Console.WriteLine($"AuctionIndexedCatalogSelfTest: passed={passed} failed={failed}; in-memory resources only, no full PVF scan or server.");
            return failed == 0 ? 0 : 1;
        }
    }
}
