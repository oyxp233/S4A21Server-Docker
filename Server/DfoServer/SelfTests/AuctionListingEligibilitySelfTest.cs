using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Infrastructure;

namespace DfoServer.SelfTests
{
    public static class AuctionListingEligibilitySelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction eligibility: {name}"); if (condition) passed++; else failed++; }
            const int account = 79301, character = 79301;
            const long now = 1792700000;
            const string hash = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
            var session = Guid.NewGuid(); var replacementSession = Guid.NewGuid();
            var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-eligibility-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                // The real script parser recognizes tags on separate lines.
                // Known noncash fixtures include explicit and absent native cash=0 cases.
                var files = new Dictionary<string, string> {
                    ["equipment/equipment.lst"] = "101 `a.equ` 102 `b.equ` 103 `cash.equ` 104 `auctionfree.equ` 105 `avatar.equ` 106 `unknown.equ` 107 `negative.equ` 108 `nocash.equ` 109 `negativefree.equ` 110 `unknownfree.equ`",
                    ["equipment/a.equ"] = "[name]\n`free sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n[cash]\n0\n",
                    ["equipment/b.equ"] = "[name]\n`sealed sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[sealing]`\n[cash]\n0\n",
                    ["equipment/cash.equ"] = "[name]\n`cash sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n[cash]\n1\n",
                    ["equipment/auctionfree.equ"] = "[name]\n`auction-free cash sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n[cash]\n1\n[auction free]\n",
                    ["equipment/avatar.equ"] = "[name]\n`ordinary codec cannot grant avatar detail`\n[equipment type]\n`[hat avatar]` 0\n[attach type]\n`[free]`\n[cash]\n0\n",
                    ["equipment/unknown.equ"] = "[name]\n`future type`\n[equipment type]\n`[future equipment]` 0\n[attach type]\n`[free]`\n[cash]\n0\n",
                    ["equipment/negative.equ"] = "[name]\n`invalid cash`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n[cash]\n-1\n",
                    ["equipment/nocash.equ"] = "[name]\n`native default cash sword`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n",
                    ["equipment/negativefree.equ"] = "[name]\n`free gate does not read cash`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[free]`\n[cash]\n-1\n[auction free]\n",
                    ["equipment/unknownfree.equ"] = "[name]\n`free tag cannot prove unknown type`\n[equipment type]\n`[future equipment]` 0\n[attach type]\n`[free]`\n[auction free]\n",
                    ["stackable/stackable.lst"] = "201 `a.stk` 202 `b.stk` 203 `c.stk` 204 `d.stk` 205 `cash.stk` 206 `nocash.stk` 207 `negative.stk`",
                    ["stackable/a.stk"] = "[name]\n`free material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n[cash]\n0\n",
                    ["stackable/b.stk"] = "[name]\n`limited material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[trade limit]`\n[trade limit max]\n3\n[cash]\n0\n",
                    ["stackable/c.stk"] = "[name]\n`account material`\n[stackable type]\n`[material]` 0\n[attach type]\n`[account]`\n[cash]\n0\n",
                    ["stackable/d.stk"] = "[name]\n`unknown policy`\n[stackable type]\n`[material]` 0\n[attach type]\n`[future policy]`\n[cash]\n0\n",
                    ["stackable/cash.stk"] = "[name]\n`cash stackable`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n[cash]\n1\n",
                    ["stackable/nocash.stk"] = "[name]\n`native default cash stackable`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n",
                    ["stackable/negative.stk"] = "[name]\n`stackable gate does not read cash`\n[stackable type]\n`[material]` 0\n[attach type]\n`[free]`\n[cash]\n-1\n"
                };
                void PackagableFixture(string kind, int id, string tag)
                {
                    string extension = kind == "equipment" ? ".equ" : ".stk";
                    files[kind + "/" + kind + ".lst"] += " " + id + " `pack" + id + extension + "`";
                    files[kind + "/pack" + id + extension] = files[kind + "/a" + extension] + tag + "[auction free]\n";
                }
                PackagableFixture("equipment", 111, "[packagable]\n0\n");
                PackagableFixture("equipment", 112, "[packagable]\ninvalid\n");
                PackagableFixture("equipment", 113, "[packagable]\n1\n");
                PackagableFixture("stackable", 208, "[packagable]\n0\n");
                PackagableFixture("stackable", 209, "[packagable]\ninvalid\n");
                PackagableFixture("stackable", 210, "[packagable]\n");
                PackagableFixture("stackable", 211, "[packagable]\n1\n[packagable]\n0\n");
                PackagableFixture("stackable", 212, "[packagable]\n-1\n");
                PackagableFixture("equipment", 115, "[usable period]\n0\n");
                PackagableFixture("stackable", 214, "[expiration date]\n0\n");
                files["equipment/equipment.lst"] += " 114 `impossible.equ`";
                files["equipment/impossible.equ"] = files["equipment/a.equ"] + "[impossible contents]\n`gift`\n[/impossible contents]\n";
                files["stackable/stackable.lst"] += " 213 `impossible.stk`";
                files["stackable/impossible.stk"] = files["stackable/a.stk"] + "[impossible contents]\n`gift`\n[/impossible contents]\n";
                foreach (int key in new[] { 3033, 3034, 3035, 3036, 3037, 3262 })
                {
                    files["stackable/stackable.lst"] += " " + key + " `feature" + key + ".stk`";
                    files["stackable/feature" + key + ".stk"] = files["stackable/a.stk"];
                }
                foreach (var entry in new[] { (3028, "material/piece_leather.stk", 1),
                    (3053, "material/seal_mire.stk", 8), (3156, "material/rubstone_1.stk", 1) })
                {
                    files["stackable/stackable.lst"] += " " + entry.Item1 + " `" + entry.Item2 + "`";
                    files["stackable/" + entry.Item2] = "[name]\n`M5 supported material`\n[stackable type]\n`[material]` "
                        + entry.Item3 + "\n[attach type]\n`[free]`\n";
                }
                var catalog = AuctionTemplateCatalog.Load(path => files.TryGetValue(path, out var value) ? value : null, hash);
                var db = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                db.Write((connection, transaction) => {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(79301,'auction-eligibility','');"
                        + "INSERT INTO characters(character_id,account_id,name,level) VALUES(79301,79301,'auction-eligibility',85);";
                    command.ExecuteNonQuery();
                });
                var lease = InventoryContext.Register(session, new InventoryService(character, account, db));
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var premium = new AuctionEntitlementSnapshot(account, null, now);
                var source = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 201, Count = 10 };
                void Set(ItemCore core) => lease.Inventory.SetItem(InventoryListType.Main, 65, core.Copy());
                AuctionNativeListingProof Native(AuctionNativeListingContext context)
                    => new(AuctionNativeProofKind.Fixture, AuctionListingEligibilityStatus.Allowed,
                        context.Template.TemplateId, context.SourceContentHash, context.Template.SourcePvfSha256,
                        AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount, false,
                        "fixture/explicit-only-count-proof", "");
                Func<AuctionNativeListingContext, AuctionNativeListingProof> proofFactory = Native;
                var eligibility = new AuctionListingEligibility(catalog, codec, context => proofFactory(context), allowFixtureProofs: true);
                AuctionListingEligibilityResult Evaluate(int qty = 3, AuctionEntitlementSnapshot rights = null)
                    => eligibility.Evaluate(lease, InventoryListType.Main, 65, qty, now, now + 86400, rights ?? premium);
                Set(source);
                var before = codec.Capture(lease.Inventory, InventoryListType.Main, 65);
                var allowed = Evaluate();
                Check("exact owned source plus explicit fixture capability can be assessed without debit", allowed.Status == AuctionListingEligibilityStatus.Allowed
                    && allowed.SourceContentHash == before.ContentHashHex && allowed.Quantity == 3
                    && allowed.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount
                    && lease.Inventory.GetItem(InventoryListType.Main, 65).Count == 10);
                Check("assessment binds owner session slot and resource batch", allowed.SessionId == session
                    && allowed.CharacterId == character && allowed.AccountId == account
                    && allowed.Slot == 65 && allowed.List == InventoryListType.Main && allowed.SourcePvfSha256 == hash);
                Check("absent packagable passes only its independent gate", catalog.Templates[201].PackagableGate == AuctionPackagableGate.Passed
                    && catalog.Templates[201].PackagableTagCount == 0);
                int packagableProofCalls = 0;
                proofFactory = context => { packagableProofCalls++; return Native(context); };
                foreach (bool activePremium in new[] { false, true })
                {
                    var rights = new AuctionEntitlementSnapshot(account, activePremium ? now + 1 : null, now);
                    foreach (int id in new[] { 111, 112, 208, 209, 210, 211 })
                    {
                        bool equipment = id < 200;
                        var item = new ItemCore { ItemKind = equipment ? ItemCore.KindEquipment : ItemCore.KindMaterial, ItemId = id, Count = equipment ? 1 : 10 };
                        Set(item);
                        int callsBefore = packagableProofCalls;
                        var denied = Evaluate(equipment ? 1 : 3, rights);
                        bool zero = id == 111 || id == 208 || id == 211;
                        Check($"packagable gate precedes premium/free native proof id={id} premium={activePremium}",
                            denied.Status == (zero ? AuctionListingEligibilityStatus.Rejected : AuctionListingEligibilityStatus.Unavailable)
                            && denied.Reason == (zero ? "NativePackagableZero" : "PackagableFieldUnavailable")
                            && packagableProofCalls == callsBefore && catalog.Templates[id].HasAuctionFree
                            && lease.Inventory.GetItem(InventoryListType.Main, 65).ToBytes().AsSpan().SequenceEqual(item.ToBytes()));
                    }
                }
                foreach (int id in new[] { 113, 212 })
                {
                    bool equipment = id < 200;
                    Set(new ItemCore { ItemKind = equipment ? ItemCore.KindEquipment : ItemCore.KindMaterial, ItemId = id, Count = equipment ? 1 : 10 });
                    int callsBefore = packagableProofCalls;
                    Check("nonzero packagable still requires the independent native proof " + id,
                        Evaluate(equipment ? 1 : 3).Status == AuctionListingEligibilityStatus.Allowed && packagableProofCalls == callsBefore + 1);
                }
                Set(source);
                proofFactory = Native;
                foreach (int id in new[] { 115, 214 })
                {
                    bool equipment = id < 200;
                    Set(new ItemCore { ItemKind = equipment ? ItemCore.KindEquipment : ItemCore.KindMaterial, ItemId = id, Count = equipment ? 1 : 10 });
                    var temporal = Evaluate(equipment ? 1 : 3, new(account, now + 1, now));
                    Check("explicit temporal0 cannot masquerade as absent tags " + id, temporal.Status == AuctionListingEligibilityStatus.Unavailable
                        && temporal.Reason == "TemplateTemporalTagsPresentUnsupported" && !catalog.Templates[id].TemporalTagsAbsent);
                }
                Set(source);
                var production = new AuctionListingEligibility(catalog, codec, Native);
                Check("fixture capability is never production default", production.Evaluate(lease, InventoryListType.Main, 65, 3, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => null;
                Check("catalog and codec success cannot bypass missing native proof", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { Kind = AuctionNativeProofKind.Unknown };
                Check("unknown native rule stays unavailable", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { TemplateId = 202 };
                Check("native proof must bind exact template", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { SourceContentHash = new string('0', 64) };
                Check("native proof must bind complete instance", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { SourcePvfSha256 = new string('0', 64) };
                Check("native proof cannot cross PVF batches", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { EvidenceReference = " " };
                Check("hashes alone are not a policy evidence reference", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { Capability = AuctionListingTransferCapability.None };
                Check("unverified transfer mutation capability stays unavailable", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = context => Native(context) with { Status = AuctionListingEligibilityStatus.Rejected, Reason = "fixture-native-denial" };
                Check("native denied class rejected with concrete reason", Evaluate().Status == AuctionListingEligibilityStatus.Rejected && Evaluate().Reason == "fixture-native-denial");
                proofFactory = context => Native(context) with { RequiresPremium = true };
                Check("proven native premium class requires active rights", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                Check("premium expiry equal to now is inactive", Evaluate(rights: new(account, now, now)).Status == AuctionListingEligibilityStatus.Rejected);
                Check("active account rights permit proven premium class", Evaluate(rights: new(account, now + 1, now)).Status == AuctionListingEligibilityStatus.Allowed);
                Check("another account premium cannot authorize source", Evaluate(rights: new(account + 1, now + 1, now)).Status == AuctionListingEligibilityStatus.Unavailable);
                Check("stale premium snapshot cannot authorize source", Evaluate(rights: new(account, now + 1, now - 1)).Status == AuctionListingEligibilityStatus.Unavailable);
                proofFactory = Native;
                Check("quantity cannot exceed actual stack", Evaluate(11).Status == AuctionListingEligibilityStatus.Rejected);
                Check("zero quantity is rejected", Evaluate(0).Status == AuctionListingEligibilityStatus.Rejected);
                var changed = source.Copy(); changed.EquipmentLockId = 1; Set(changed);
                Check("equipment lock blocks listing before any proof", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                changed = source.Copy(); changed.SortLockFlag = 1; Set(changed);
                Check("sort lock blocks listing", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                changed = source.Copy(); changed.TradeRestriction = 1; Set(changed);
                Check("instance restriction overrides free template", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                changed = source.Copy(); changed.ExpireTime = checked((int)now + 100); Set(changed);
                Check("unexpired timed item is not inferred from direct-trade permission", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                changed.ExpireTime = 1; Set(changed);
                Check("unproven expiration sentinel cannot silently pass", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                changed = source.Copy(); changed.ItemId = 999; Set(changed);
                Check("unknown exact template is unavailable", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                changed = source.Copy(); changed.ItemId = 202; changed.StackTradeCount = 0; Set(changed);
                Check("exhausted transfer counter is rejected", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                changed.StackTradeCount = 2; Set(changed);
                Check("remaining trade count does not invent auction decrement timing", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                changed = source.Copy(); changed.ItemId = 203; Set(changed);
                Check("account bound template cannot exploit own-account destination", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                changed = source.Copy(); changed.ItemId = 204; Set(changed);
                Check("unknown attach policy does not become free", Evaluate().Status == AuctionListingEligibilityStatus.Unavailable);
                Set(new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 102, SealFlag = 0 });
                Check("unsealed sealing equipment cannot list", Evaluate(1).Status != AuctionListingEligibilityStatus.Allowed);
                Set(new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 102, SealFlag = 1 });
                Check("sealed equipment still needs native proof and can preserve opaque state", Evaluate(1).Status == AuctionListingEligibilityStatus.Allowed);
                Check("equipment count cannot be treated as a stack", Evaluate(2).Status == AuctionListingEligibilityStatus.Rejected);
                Set(source);
                Check("equipped list is outside this transfer capability", eligibility.Evaluate(lease, InventoryListType.Equipment, 0, 1, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Unavailable);
                Check("virtual wallet slot is not an ordinary item source", eligibility.Evaluate(lease, InventoryListType.Main, 354, 1, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Unavailable);
                var nativeEligibility = new AuctionListingEligibility(catalog, codec, AuctionNativeListingRuleResolver.Resolve);
                foreach (int id in new[] { 3028, 3053, 3156 })
                {
                    var item = new ItemCore { ItemId = id, ItemKind = ItemCore.KindMaterial, Count = 8 };
                    Set(item);
                    var exact = nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 3, now, now + 86400, premium);
                    Check("three-key native proof passes unchanged owned Main source " + id,
                        exact.Status == AuctionListingEligibilityStatus.Allowed && exact.NativeProof.Kind == AuctionNativeProofKind.VerifiedA21
                        && exact.Quantity == 3 && exact.Slot == 65 && exact.SessionId == session
                        && exact.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount
                        && codec.MatchesSource(lease.Inventory, InventoryListType.Main, 65, exact.Source));
                    Check("three-key native proof cannot exceed owned count " + id,
                        nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 9, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Rejected);
                    item.SortLockFlag = 1; Set(item);
                    Check("three-key native proof cannot bypass source lock " + id,
                        nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 3, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Rejected);
                    item.SortLockFlag = 0; item.ExpireTime = checked((int)(now + 10)); Set(item);
                    Check("three-key native proof cannot bypass instance expiry " + id,
                        nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 3, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Unavailable);
                }
                for (short slot = InventoryService.MainVirtualCurrencySlotStart; slot <= InventoryService.MainVirtualCurrencySlotEnd; slot++)
                    Check("production native adapter refuses virtual currency slot " + slot,
                        nativeEligibility.Evaluate(lease, InventoryListType.Main, slot, 1, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Unavailable);
                AuctionNativeListingContext NativeContext(int id, bool active = false)
                {
                    var core = new ItemCore { ItemId = id, ItemKind = id < 200 ? ItemCore.KindEquipment : ItemCore.KindMaterial, Count = 1 };
                    Set(core); var envelope = codec.Capture(lease.Inventory, InventoryListType.Main, 65);
                    if (!catalog.TryGet(id, out var template)) throw new InvalidOperationException("Native fixture template missing.");
                    return new(account, character, template, envelope, envelope.ContentHashHex, 1, now, now + 86400, active);
                }
                var normalContext = NativeContext(101);
                var verified = AuctionNativeListingRuleResolver.Resolve(normalContext);
                Check("coupon component passes known noncash equipment", AuctionNativeListingRuleResolver.EvaluateCoupon(normalContext).Status == AuctionCouponGateStatus.Passed);
                Check("free equipment uses resource and instance eligibility", verified.Kind == AuctionNativeProofKind.VerifiedA21
                    && verified.Status == AuctionListingEligibilityStatus.Allowed && verified.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount
                    && verified.SourceContentHash == normalContext.SourceContentHash
                    && verified.SourcePvfSha256 == hash && verified.EvidenceReference.Contains("VA0232FD90", StringComparison.Ordinal));
                Check("production adapter accepts owned free equipment outside old whitelist", nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 1, now, now + 86400, premium).Status == AuctionListingEligibilityStatus.Allowed);
                var cashContext = NativeContext(103);
                var cashDenied = AuctionNativeListingRuleResolver.Resolve(cashContext);
                Check("raw PVF cash equipment requires coupon", cashDenied.Status == AuctionListingEligibilityStatus.Rejected && cashDenied.RequiresPremium);
                Check("active premium passes cash-equipment coupon component", AuctionNativeListingRuleResolver.EvaluateCoupon(cashContext with { HasActiveAuctionPremium = true }).Status == AuctionCouponGateStatus.Passed);
                Check("validated premium permits owned tradable cash equipment", nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 1, now, now + 86400, new(account, now + 1, now)).Status == AuctionListingEligibilityStatus.Allowed);
                var freeCashContext = NativeContext(104);
                Check("explicit auction-free tag bypasses extra cash coupon gate", freeCashContext.Template.HasAuctionFree
                    && AuctionNativeListingRuleResolver.EvaluateCoupon(freeCashContext).Status == AuctionCouponGateStatus.Passed);
                Check("tradable auction-free equipment is accepted", AuctionNativeListingRuleResolver.Resolve(freeCashContext).Status == AuctionListingEligibilityStatus.Allowed);
                var avatarContext = NativeContext(105);
                Check("existing avatar type mapping matches native coupon branch", AuctionNativeListingRuleResolver.Resolve(avatarContext).RequiresPremium);
                Check("coupon never bypasses unsupported avatar template detail", nativeEligibility.Evaluate(lease, InventoryListType.Main, 65, 1, now, now + 86400, new(account, now + 1, now)).Status == AuctionListingEligibilityStatus.Unavailable);
                Check("unproven equipment type stays unknown even with premium", AuctionNativeListingRuleResolver.Resolve(NativeContext(106, true)).Kind == AuctionNativeProofKind.Unknown);
                Check("negative cash field is not silently treated as normal equipment", AuctionNativeListingRuleResolver.Resolve(NativeContext(107)).Status == AuctionListingEligibilityStatus.Unavailable);
                Check("known equipment premium gate short circuits an irrelevant negative cash field", AuctionNativeListingRuleResolver.EvaluateCoupon(NativeContext(107, true)).Status == AuctionCouponGateStatus.Passed);
                var noCashEquipment = NativeContext(108);
                Check("missing cash tag uses positively proved native fresh-model zero", noCashEquipment.Template.CashInteger == 0
                    && AuctionNativeListingRuleResolver.EvaluateCoupon(noCashEquipment).Status == AuctionCouponGateStatus.Passed);
                Check("known equipment auction-free gate short circuits irrelevant negative cash", AuctionNativeListingRuleResolver.EvaluateCoupon(NativeContext(109)).Status == AuctionCouponGateStatus.Passed);
                Check("auction-free never proves an unknown equipment class", AuctionNativeListingRuleResolver.Resolve(NativeContext(110)).Kind == AuctionNativeProofKind.Unknown);
                Check("ordinary stackable coupon component uses failed-equipment-cast branch", AuctionNativeListingRuleResolver.EvaluateCoupon(NativeContext(205)).Status == AuctionCouponGateStatus.Passed);
                var noCashStackable = NativeContext(206);
                Check("ordinary stackable missing cash passes the coupon component", noCashStackable.Template.CashInteger == 0
                    && AuctionNativeListingRuleResolver.EvaluateCoupon(noCashStackable).Status == AuctionCouponGateStatus.Passed);
                Check("ordinary stackable coupon component does not read negative cash", AuctionNativeListingRuleResolver.EvaluateCoupon(NativeContext(207)).Status == AuctionCouponGateStatus.Passed);
                foreach (int id in new[] { 101, 103, 104, 108, 109, 201, 205, 206, 207 })
                {
                    var unverified = AuctionNativeListingRuleResolver.Resolve(NativeContext(id, true));
                    Check("free ordinary resources have a complete production capability " + id,
                        unverified.Kind == AuctionNativeProofKind.VerifiedA21 && unverified.Status == AuctionListingEligibilityStatus.Allowed
                        && unverified.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount);
                }
                Check("ordinary free stack needs no factory key whitelist",
                    AuctionNativeListingRuleResolver.Resolve(NativeContext(201)).Status == AuctionListingEligibilityStatus.Allowed);
                Check("restricted stack attach class never falls back to free",
                    AuctionNativeListingRuleResolver.Resolve(NativeContext(202)).Status == AuctionListingEligibilityStatus.Rejected);
                foreach (int key in new[] { 3033, 3034, 3035, 3036, 3037, 3262 })
                {
                    var feature = AuctionNativeListingRuleResolver.Resolve(NativeContext(key, true));
                    Check("free material cannot be refused solely by its key " + key,
                        feature.Kind == AuctionNativeProofKind.VerifiedA21 && feature.Status == AuctionListingEligibilityStatus.Allowed
                        && feature.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount);
                }
                foreach (int id in new[] { 114, 213 })
                {
                    var impossible = AuctionNativeListingRuleResolver.Resolve(NativeContext(id, true));
                    Check("gift restriction does not prohibit auction " + id,
                        impossible.Status == AuctionListingEligibilityStatus.Allowed && impossible.Kind == AuctionNativeProofKind.VerifiedA21);
                }
                Check("native rule cannot use unrelated source hash", AuctionNativeListingRuleResolver.Resolve(normalContext with { SourceContentHash = new string('0', 64) }).Status == AuctionListingEligibilityStatus.Unavailable);
                catalog.TryGet(103, out var otherTemplate);
                Check("native rule cannot apply another template to a valid source envelope", AuctionNativeListingRuleResolver.Resolve(normalContext with { Template = otherTemplate }).Status == AuctionListingEligibilityStatus.Unavailable);
                Set(source);
                proofFactory = context => {
                    var different = source.Copy(); different.Count = 9; Set(different);
                    return Native(context);
                };
                Check("source is rechecked after native resolver invocation", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
                proofFactory = Native;
                InventoryContext.Register(replacementSession, new InventoryService(character, account, db));
                Check("old session cannot authorize after owner replacement", Evaluate().Status == AuctionListingEligibilityStatus.Rejected);
            }
            catch (Exception exception) { Console.WriteLine("[FAIL] unexpected auction eligibility exception: " + exception); failed++; }
            finally { InventoryContext.Unregister(session); InventoryContext.Unregister(replacementSession); }
            Console.WriteLine($"Auction listing eligibility: {passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
