using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.SelfTests
{
    public static class AuctionNativeCategorySelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction category: {name}"); if (ok) passed++; else failed++; }
            try
            {
                const string hash = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
                var texts = new Dictionary<string, string>();
                var equipmentList = new List<string>(); var stackList = new List<string>();
                int equipmentId = 1000, stackId = 2000;
                int Equipment(string type, int? subtype = null, string jobs = null, string itemCategory = null)
                {
                    int id = ++equipmentId;
                    equipmentList.Add($"{id} `e{id}.equ`");
                    texts[$"equipment/e{id}.equ"] = "[name]\n`fixture`\n[equipment type]\n`[" + type + "]` 0\n"
                        + (subtype.HasValue ? "[sub type]\n" + subtype + "\n" : "")
                        + (jobs == null ? "" : "[usable job]\n" + jobs + "\n")
                        + (itemCategory == null ? "" : "[item category]\n`[" + itemCategory + "]`\n");
                    return id;
                }
                int Stack(string type, string itemCategory = null)
                {
                    int id = ++stackId;
                    stackList.Add($"{id} `s{id}.stk`");
                    texts[$"stackable/s{id}.stk"] = "[name]\n`fixture`\n[stackable type]\n`[" + type + "]` 0\n"
                        + (itemCategory == null ? "" : "[item category]\n`[" + itemCategory + "]`\n");
                    return id;
                }
                int sword = Equipment("weapon", 0, "`[swordman]`"), katana = Equipment("weapon", 1, "`[at swordman]`"),
                    beam = Equipment("weapon", 5, "`[demonic swordman]`"), knight = Equipment("weapon", 3, "`[knight]`"),
                    tonfa = Equipment("weapon", 5, "`[at fighter]`"), auto = Equipment("weapon", 1, "`[at gunner]`"),
                    broom = Equipment("weapon", 4, "`[creator mage]`"), axe = Equipment("weapon", 4, "`[priest]`"),
                    wand = Equipment("weapon", 3, "`[thief]`"), halberd = Equipment("weapon", 1, "`[demonic lancer]`"),
                    missingJobs = Equipment("weapon", 0), futureJob = Equipment("weapon", 0, "`[future job]`"),
                    futureSubtype = Equipment("weapon", 99, "`[swordman]`"), allWeapon = Equipment("weapon", 0, "`[all]`"),
                    partialJobs = Equipment("weapon", 0, "`[fighter]` `[future job]`"),
                    supportAll = Equipment("support", 0, "`[all]`"), supportSword = Equipment("support", 0, "`[swordman]`"),
                    supportMissing = Equipment("support"), stoneFemale = Equipment("magic stone", 0, "`[at swordman]`"),
                    title = Equipment("title name"), amulet = Equipment("amulet"), ring = Equipment("ring"), wrist = Equipment("wrist"),
                    creature = Equipment("creature"), red = Equipment("artifact red"), blue = Equipment("artifact blue"), green = Equipment("artifact green"),
                    avatar = Equipment("coat avatar"), flag = Equipment("flag"), unknownEquipment = Equipment("future equipment", 0, "`[all]`"),
                    armorMissing = Equipment("coat"), armorFuture = Equipment("coat", 99);
                var armor = new int[5, 5];
                string[] slots = { "coat", "shoulder", "pants", "shoes", "waist" };
                for (int material = 0; material < 5; material++)
                    for (int slot = 0; slot < 5; slot++) armor[material, slot] = Equipment(slots[slot], material);
                int materialStack = Stack("material"), expert = Stack("material expert job"), recipe = Stack("recipe"),
                    waste = Stack("waste"), expertPotion = Stack("expert town potion"), throwing = Stack("throw"),
                    legacy = Stack("multi upgradable legacy"), quest = Stack("quest receive"), etc = Stack("booster selection"),
                    feed = Stack("feed"), creatureStack = Stack("stackable creature"),
                    unknownStack = Stack("future stackable", "material"), suffixStack = Stack("material malicious");
                texts["equipment/equipment.lst"] = string.Join("\n", equipmentList);
                texts["stackable/stackable.lst"] = string.Join("\n", stackList);
                AuctionTemplateCatalog Load(string source) => AuctionTemplateCatalog.Load(path => texts.TryGetValue(path, out var value) ? value : null, source);
                var catalog = Load(hash);
                var resolver = new AuctionNativeCategoryResolver(catalog);
                AuctionQueryMatch Match(ushort category, int id) => resolver.Resolve(category).Matches(catalog.Templates[id]);
                void Yes(ushort category, int id, string name) => Check(name, Match(category, id) == AuctionQueryMatch.Match);
                void No(ushort category, int id, string name) => Check(name, Match(category, id) == AuctionQueryMatch.NoMatch);
                void Unknown(ushort category, int id, string name) => Check(name, Match(category, id) == AuctionQueryMatch.Unknown);
                Check("rule binds native code exact PVF and explicit private policy evidence", resolver.Resolve(10000) is var rule
                    && rule.NativeCategory == 10000 && rule.SourcePvfSha256 == hash && rule.EvidenceReference.Contains(AuctionNativeCategoryResolver.PolicyVersion));
                Check("universal40119 remains engine-owned", resolver.Resolve(40119) == null);
                Check("PVF master10 is not native weapon code", resolver.Resolve(10) == null);
                Check("unknown native category has no false predicate", resolver.Resolve(65535) == null);
                foreach (ushort unsupported in new ushort[] { 31001, 31002, 31101, 31303, 33000, 33001, 33002, 33003, 34000 })
                    Check("unproved category stays unavailable " + unsupported, resolver.Resolve(unsupported) == null);
                Check("foreign same-hash projection is not the exact template", resolver.Resolve(10000).Matches(Load(hash).Templates[sword]) == AuctionQueryMatch.Unknown);
                Check("foreign PVF projection is unknown", resolver.Resolve(10000).Matches(Load(new string('a', 64)).Templates[sword]) == AuctionQueryMatch.Unknown);
                Check("missing template is unknown", resolver.Resolve(10000).Matches(null) == AuctionQueryMatch.Unknown);
                Yes(10000, sword, "broad weapon uses real equipment type");
                Yes(10102, sword, "sword subtype0 is short sword");
                Yes(10103, katana, "female swordman subtype1 remains sword family");
                Yes(10106, beam, "beam sword subtype is5 rather than4");
                Yes(10105, knight, "knight's sword group is explicit product inclusion");
                Yes(10206, tonfa, "fighter tonfa subtype5 stays job-relative");
                Yes(10303, auto, "gunner subtype1 is automatic rather than katana");
                No(10103, auto, "same subtype never replaces weapon job family");
                Yes(10406, broom, "creator mage is included with mage");
                Yes(10506, axe, "priest subtype4 selects axe");
                Yes(10604, wand, "thief wand subtype3 is not array index2");
                Yes(10803, halberd, "native lancer family uses real tag and subtype1");
                Unknown(10101, missingJobs, "missing jobs cannot mean no matching weapons");
                Unknown(10101, futureJob, "future job is not silently excluded");
                Unknown(10102, futureSubtype, "future subtype cannot become false complete category");
                Yes(10101, allWeapon, "all job policy includes family parent");
                Unknown(10102, allWeapon, "all job cannot assign global weapon subtype");
                Unknown(10101, partialJobs, "known nonmatch plus unknown job is unknown");
                Yes(10201, partialJobs, "positive family proof remains a positive match");
                Yes(10000, missingJobs, "broad weapon does not require a job predicate");
                Unknown(10000, unknownEquipment, "unknown equipment class is not other");
                No(10000, materialStack, "known stackable family is disjoint from equipment weapon");
                ushort[] armorRoots = { 11001, 11100, 11200, 11300, 11400 };
                for (int m = 0; m < 5; m++)
                    for (int slot = 0; slot < 5; slot++)
                    {
                        Yes(11000, armor[m, slot], $"armor broad material{m} slot{slot}");
                        Yes(armorRoots[m], armor[m, slot], $"armor material root{m} slot{slot}");
                        Yes((ushort)(armorRoots[m] + slot + 1), armor[m, slot], $"armor exact leaf{m} slot{slot}");
                    }
                No(11002, armor[1, 0], "leather coat is not cloth coat");
                No(11003, armor[0, 0], "cloth coat is not shoulder");
                Unknown(11001, armorMissing, "missing armor material stays unknown");
                Unknown(11001, armorFuture, "future armor material stays unknown");
                Yes(11000, armorFuture, "broad armor only needs known equipment slot");
                Yes(12000, title, "accessory root includes its title child by policy");
                Yes(12004, title, "title is exact type"); Yes(12001, amulet, "amulet native child");
                Yes(12002, ring, "ring native child"); Yes(12003, wrist, "wrist native child");
                No(12000, avatar, "avatar not silently mapped to accessory");
                No(32000, flag, "flag not silently mapped to special equipment");
                Yes(32000, supportAll, "support in special root"); Yes(32100, stoneFemale, "magic stone exact root");
                Yes(32002, supportSword, "support male sword job leaf");
                No(32003, supportSword, "special female job leaf is not male alias");
                Yes(32102, stoneFemale, "magic stone female job leaf");
                Yes(32015, supportAll, "all job support includes lancer leaf");
                Unknown(32002, supportMissing, "missing special-equipment jobs are unknown");
                Yes(13000, waste, "consumable root explicit union"); Yes(13001, waste, "waste exact consumable family");
                Yes(13001, expertPotion, "expert town potion explicit consumable inclusion");
                Yes(13002, materialStack, "material leaf"); Yes(13002, expert, "profession material also in material family");
                Yes(33004, expert, "profession material exact leaf"); No(33004, materialStack, "ordinary material is not profession material");
                Yes(13003, throwing, "throw alias explicit family"); Yes(13004, legacy, "multi-upgradable legacy explicit family");
                Yes(13005, quest, "quest receive explicit quest inclusion"); Yes(13006, etc, "selection booster explicit etc family");
                Yes(31000, recipe, "recipe root uses stackable type rather than output guess"); No(13000, recipe, "recipe gets its separate root");
                Yes(14000, creature, "pet root actual creature"); Yes(14001, red, "red artifact native child");
                Yes(14002, blue, "blue artifact native child"); Yes(14003, green, "green artifact native child");
                Yes(14000, feed, "pet root explicitly includes feed"); Yes(14000, creatureStack, "pet root explicitly includes stackable creature");
                No(14000, waste, "ordinary waste not pet by path or name");
                Unknown(13002, unknownStack, "item-category label cannot replace unknown stackable type");
                Unknown(13002, suffixStack, "unknown token suffix cannot match a familiar prefix");

                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var engine = new AuctionQueryEngine(catalog, codec, resolver.Resolve);
                const long now = 1792700000;
                var principal = new AuctionQueryPrincipal(Guid.Parse("8bd67102-1010-4000-8000-000000000003"), 1, 80101, 80101);
                AuctionQueryCriteria Criteria(ushort code) => AuctionQueryEngine.Normalize(new AuctionSearchWithoutItemKeyRequest(0, code, 7, 0, 31, 0, 0, 0, 0, 0, 0, 31)).Value;
                AuctionOrderSnapshot Order(int id)
                {
                    var core = new ItemCore { ItemId = id, ItemKind = ItemCore.KindEquipment, Value = 123 };
                    var envelope = codec.Serialize(AuctionItemEnvelope.Create(core.ToBytes(), "", "{}"));
                    return new(id, 1, AuctionOrderState.Active, new(80101, 80101, "seller"), envelope, envelope,
                        1, 1, null, 100, null, 0, 10000, now - 1, now + 3600, null, "account:80101", AuctionEconomyPolicy.PolicyVersion, 5, 0, 0, 100);
                }
                Check("supported category empty result is genuinely available", engine.SelectCandidates(Array.Empty<AuctionOrderSnapshot>(), principal, Criteria(10102), now).IsAvailable);
                Check("unsupported category empty result is unavailable", engine.SelectCandidates(Array.Empty<AuctionOrderSnapshot>(), principal, Criteria(34000), now).Status == AuctionQueryStatus.Unavailable);
                var found = engine.SelectCandidates(new[] { Order(sword), Order(auto) }, principal, Criteria(10102), now);
                Check("real query applies catalog-bound resolver", found.IsAvailable && found.Value.Length == 1 && found.Value[0].TemplateId == sword);
                Check("one unknown membership fails query rather than hiding candidate", engine.SelectCandidates(new[] { Order(sword), Order(futureSubtype) }, principal, Criteria(10102), now).Status == AuctionQueryStatus.Unavailable);
                Check("title-job input remains existing sender normalization", Criteria(12005).NativeCategory == 12004);
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected auction category exception: " + ex); failed++; }
            Console.WriteLine($"AuctionNativeCategorySelfTest: passed={passed} failed={failed}; pure catalog/query policy, native backend membership not inferred.");
            return failed == 0 ? 0 : 1;
        }
    }
}
