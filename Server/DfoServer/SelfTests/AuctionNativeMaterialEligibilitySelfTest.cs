using System;
using System.Collections.Generic;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Game.Auction.Storage;
using DfoServer.Network.Handlers.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.SelfTests
{
    // Resource-based eligibility and native price regression. No archive/DB writes.
    public static class AuctionNativeMaterialEligibilitySelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] auction native material: {name}"); if (ok) passed++; else failed++; }
            const string hash = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";
            const long now = 1792700000;
            try
            {
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var inventory = new InventoryService(79304, 79304);
                AuctionNativeListingContext Context(bool indexed, int id, string path, string declaration,
                    string tags = "", string pvf = hash, byte kind = ItemCore.KindMaterial)
                {
                    var files = new Dictionary<string, string> {
                        ["equipment/equipment.lst"] = "9999 `dummy.equ`",
                        ["equipment/dummy.equ"] = "[name]\n`dummy`\n[equipment type]\n`[weapon]` 0\n",
                        ["stackable/stackable.lst"] = id + " `" + path + "`",
                        ["stackable/" + path] = "[name]\n`fixture`\n" + declaration
                            + "[attach type]\n`[free]`\n" + tags
                    };
                    string Read(string resource) => files.TryGetValue(resource, out var text) ? text : null;
                    var catalog = indexed ? AuctionTemplateCatalog.LoadIndexed(Read, pvf) : AuctionTemplateCatalog.Load(Read, pvf);
                    var item = new ItemCore { ItemId = id, ItemKind = kind, Count = 8 };
                    if (!inventory.SetItem(InventoryListType.Main, 65, item)) throw new InvalidOperationException("Cannot set fixture source.");
                    var source = codec.Capture(inventory, InventoryListType.Main, 65);
                    return new(79304, 79304, catalog.Templates[id], source, source.ContentHashHex, 3, now, now + 86400, false);
                }
                AuctionNativeListingContext WeaponContext(bool indexed, int id = 27602,
                    string declaration = "[equipment type]\n`[weapon]` 20\n", string tags = "",
                    string attach = "[attach type]\n`[free]`\n", string pvf = hash,
                    byte kind = ItemCore.KindEquipment)
                {
                    var files = new Dictionary<string, string> {
                        ["equipment/equipment.lst"] = id + " `weapon/m8-fixture.equ`",
                        ["equipment/weapon/m8-fixture.equ"] = "[name]\n`M8 fixture`\n" + declaration + attach + tags,
                        ["stackable/stackable.lst"] = "# no stackable entries"
                    };
                    string Read(string resource) => files.TryGetValue(resource, out var text) ? text : null;
                    var catalog = indexed ? AuctionTemplateCatalog.LoadIndexed(Read, pvf) : AuctionTemplateCatalog.Load(Read, pvf);
                    var item = new ItemCore { ItemId = id, ItemKind = kind, Value = 0x12345678, Durability = 35 };
                    if (!inventory.SetItem(InventoryListType.Main, 65, item)) throw new InvalidOperationException("Cannot set M8 fixture.");
                    var source = codec.Capture(inventory, InventoryListType.Main, 65);
                    return new(79304, 79304, catalog.Templates[id], source, source.ContentHashHex, 1, now, now + 86400, false);
                }
                // Synthetic catalog/core inputs test the finite proof boundary;
                // these are not an owned runtime Avatar or a native preview.
                AuctionNativeListingContext WaistAvatarContext(bool indexed, int id = 53585,
                    string declaration = "[equipment type]\n`[waist avatar]` 0\n", string tags = "",
                    string attach = "[attach type]\n`[free]`\n", string pvf = hash,
                    byte kind = ItemCore.KindAvatar, int expiry = 0, string relativePath = null)
                {
                    relativePath ??= $"character/mage/avatar/belt/abelt_{id}.equ";
                    var files = new Dictionary<string, string> {
                        ["equipment/equipment.lst"] = id + " `" + relativePath + "`",
                        ["equipment/" + relativePath] = "[name]\n`M9 fixture`\n" + declaration + attach + tags,
                        ["stackable/stackable.lst"] = "# no stackable entries"
                    };
                    string Read(string resource) => files.TryGetValue(resource, out var text) ? text : null;
                    var catalog = indexed ? AuctionTemplateCatalog.LoadIndexed(Read, pvf) : AuctionTemplateCatalog.Load(Read, pvf);
                    var core = new ItemCore { ItemId = id, ItemKind = kind, AvatarUid = 654321,
                        AbilityNo = 321, ExpireTime = expiry, SealFlag = 1 };
                    var sockets = new byte[30];
                    for (int i = 0; i < sockets.Length; i++) sockets[i] = (byte)(i + 1);
                    string detail = JsonSerializer.Serialize(new { FormatVersion = 1, Kind = "avatar", Avatar = new {
                        ExpireDate = 0, ClearAvatarId = 0, JewelSocket = sockets,
                        Color1 = (ushort)0x1234, Color2 = (ushort)0xABCD, DeleteDate = 0 } });
                    var source = AuctionItemEnvelope.Create(core.ToBytes(), detail, "{}");
                    return new(79304, 79304, catalog.Templates[id], source, source.ContentHashHex, 1, now, now + 86400, true);
                }
                foreach (bool indexed in new[] { false, true })
                {
                    foreach (var context in new[] {
                        WeaponContext(indexed, 27599), WeaponContext(indexed, 27674),
                        WeaponContext(indexed, pvf:new string('b',64)),
                        WeaponContext(indexed, declaration:"[equipment type]\n`[title name]` 0\n"),
                        WeaponContext(indexed, declaration:"[equipment type]\n`[coat]` 0\n"),
                        WeaponContext(indexed, tags:"[impossible contents]\n`gift`\n[/impossible contents]\n"),
                        WaistAvatarContext(indexed, 53584), WaistAvatarContext(indexed, 53589),
                        WaistAvatarContext(indexed, pvf:new string('b',64)),
                        WaistAvatarContext(indexed, relativePath:"avatar/another-resource.equ"),
                        WaistAvatarContext(indexed, declaration:"[equipment type]\n`[coat avatar]` 0\n"),
                        WaistAvatarContext(indexed, declaration:"[equipment type]\n`[waist avatar]` 1\n"),
                        WaistAvatarContext(indexed, tags:"[cash]\n1\n"),
                        WaistAvatarContext(indexed, tags:"[auction free]\n") })
                        Check("tradable equipment/Avatar is independent of key path and archive " + context.Template.TemplateId + indexed,
                            AuctionNativeListingRuleResolver.Resolve(context).Status == AuctionListingEligibilityStatus.Allowed);
                    foreach (byte kind in new[] { ItemCore.KindConsumable, ItemCore.KindMaterial, ItemCore.KindQuest,
                        ItemCore.KindCreatureConsumable, ItemCore.KindAvatarEmblem, ItemCore.KindExpertJobMaterial,
                        ItemCore.KindSpecialMaterial, ItemCore.KindGuardianGem })
                    {
                        int id = 80000 + kind;
                        var context = Context(indexed, id, "new-resource.stk", "[stackable type]\n`[material]` 8\n",
                            pvf:new string('b',64), kind:kind);
                        Check("free stack kind has no material/key whitelist " + kind + indexed,
                            AuctionNativeListingRuleResolver.Resolve(context).Status == AuctionListingEligibilityStatus.Allowed);
                        var item = codec.DecodeStrict(context.Source);
                        byte space = kind == ItemCore.KindCreatureConsumable ? (byte)InventoryListType.Pet
                            : kind == ItemCore.KindGuardianGem ? (byte)InventoryListType.GuildMedal : (byte)InventoryListType.Main;
                        var request = new AuctionRegisterRequest(space,65,(uint)id,3,10,100,100,24,default,default,default);
                        AuctionNativeMutationResult Normalize(AuctionRegisterRequest value) => AuctionNativeRegistrationAdapter.Normalize(
                            new AuctionParty(79304,79304,"fixture"), Guid.NewGuid(), value, item, context.Template);
                        var unit = Normalize(request); var aggregate = Normalize(request with { Wire12=30, Wire16=300 });
                        Check("source uses its real carried bag and slot " + kind + indexed,
                            unit.IsReady && unit.Request.SourceList == (InventoryListType)space && unit.Request.SourceSlot == 65);
                        Check("both A21 sender branches normalize to identical aggregate terms " + kind + indexed,
                            unit.IsReady && aggregate.IsReady && unit.Request.OpeningBidGold == 30
                            && unit.Request.BuyoutGold == 300 && aggregate.Request.OpeningBidGold == 30
                            && aggregate.Request.BuyoutGold == 300);
                        Check("inconsistent price relation is rejected " + kind + indexed,
                            Normalize(request with { Wire16=299 }).Status == AuctionNativeMutationStatus.Rejected);
                        var fixedPrice = Normalize(request with { Wire12=uint.MaxValue });
                        Check("piece price is frozen once for every free stack kind " + kind + indexed,
                            fixedPrice.IsReady && fixedPrice.Request.UnitPriceGold == 100 && fixedPrice.Request.BuyoutGold == 300);
                        Check("aggregate overflow rejected before assets " + kind + indexed,
                            Normalize(request with { Wire12=uint.MaxValue,Wire16=int.MaxValue,Wire20=int.MaxValue }).Status
                                == AuctionNativeMutationStatus.Rejected);
                    }
                    foreach (string tag in new[] { "auction", "trade", "all" })
                        Check("explicit impossible content blocks auction " + tag + indexed,
                            AuctionNativeListingRuleResolver.Resolve(WeaponContext(indexed,
                                tags:"[impossible contents]\n`"+tag+"`\n[/impossible contents]\n")).Status == AuctionListingEligibilityStatus.Rejected);
                    foreach (int id in new[] { 53585, 53586, 53587, 53588 })
                    {
                        var context = WaistAvatarContext(indexed, id);
                        var proof = AuctionNativeListingRuleResolver.Resolve(context);
                        Check("M9 exact free waist requires premium and cap2 " + id + indexed,
                            proof.Kind == AuctionNativeProofKind.VerifiedA21 && proof.Status == AuctionListingEligibilityStatus.Allowed
                            && proof.RequiresPremium && proof.Capability == AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased
                            && proof.SourceContentHash == context.SourceContentHash && proof.SourcePvfSha256 == hash
                            && context.Template.HasSingleNativeWaistAvatarZeroDeclaration);
                        var absent = AuctionNativeListingRuleResolver.Resolve(context with { HasActiveAuctionPremium = false });
                        Check("M9 free Avatar without auction-free still needs active premium " + id + indexed,
                            absent.Status == AuctionListingEligibilityStatus.Rejected && absent.RequiresPremium
                            && absent.Capability == AuctionListingTransferCapability.None);
                        string before = codec.Serialize(context.Source);
                        var final = new AuctionTransferableItemAdapter(codec).Transform(context.Source, 1, false);
                        var expectedCore = codec.DecodeStrict(context.Source); expectedCore.AvatarUid = 0;
                        Check("M9 cap2 reuses full99 and all detail while rebasing only UID " + id + indexed,
                            final.NativeCore99.AsSpan().SequenceEqual(expectedCore.ToBytes())
                            && final.DetailJson == context.Source.DetailJson && final.ExtensionsJson == context.Source.ExtensionsJson
                            && codec.DecodeStrict(final).AbilityNo == 321 && codec.Serialize(context.Source) == before);
                        var request = new AuctionRegisterRequest(1, 0, checked((uint)id), 1, 100, 300, 300, 24, default, default, default);
                        var normalized = AuctionNativeRegistrationAdapter.Normalize(new AuctionParty(79304, 79304, "M9 fixture"),
                            new Guid("26ea3782-a439-4a68-b786-eb783dd41422"), request, codec.DecodeStrict(context.Source), context.Template);
                        Check("M9 Source1 Q1 reaches existing aggregate price branch " + id + indexed,
                            normalized.IsReady && normalized.Request.SourceList == InventoryListType.Avatar
                            && normalized.Request.OpeningBidGold == 100 && normalized.Request.BuyoutGold == 300
                            && normalized.Request.UnitPriceGold == null);
                        Check("M9 proof cannot cover Q2 or another source hash " + id + indexed,
                            AuctionNativeListingRuleResolver.Resolve(context with { Quantity = 2 }).Status == AuctionListingEligibilityStatus.Rejected
                            && AuctionNativeListingRuleResolver.Resolve(context with { SourceContentHash = new string('0',64) }).Kind == AuctionNativeProofKind.Unknown);
                    }
                    var avatarNegatives = new (string Name, AuctionNativeListingContext Context)[] {
                        ("missing argument", WaistAvatarContext(indexed, declaration:"[equipment type]\n`[waist avatar]`\n")),
                        ("extra argument", WaistAvatarContext(indexed, declaration:"[equipment type]\n`[waist avatar]` 0 0\n")),
                        ("duplicate type", WaistAvatarContext(indexed, declaration:"[equipment type]\n`[waist avatar]` 0\n[equipment type]\n`[waist avatar]` 0\n")),
                        ("nested type", WaistAvatarContext(indexed, declaration:"[outer]\n[equipment type]\n`[waist avatar]` 0\n[/outer]\n")),
                        ("duplicate attach", WaistAvatarContext(indexed, attach:"[attach type]\n`[free]`\n[attach type]\n`[free]`\n")),
                        ("usable period", WaistAvatarContext(indexed, tags:"[usable period]\n0\n")),
                        ("expiration", WaistAvatarContext(indexed, tags:"[expiration date]\n0\n")),
                        ("after unsealing", WaistAvatarContext(indexed, tags:"[usable period after unsealing]\n0\n")),
                        ("ordinary equipment core", WaistAvatarContext(indexed, kind:ItemCore.KindEquipment)),
                        ("timed core", WaistAvatarContext(indexed, expiry:1))
                    };
                    foreach (var negative in avatarNegatives)
                    {
                        var proof = AuctionNativeListingRuleResolver.Resolve(negative.Context);
                        Check("M9 " + negative.Name + " cannot get cap2 despite premium " + indexed,
                            proof.Kind == AuctionNativeProofKind.Unknown && proof.Status == AuctionListingEligibilityStatus.Unavailable
                            && proof.Capability == AuctionListingTransferCapability.None);
                    }
                    Check("M9 packagable0 remains a native refusal despite premium " + indexed,
                        AuctionNativeListingRuleResolver.Resolve(WaistAvatarContext(indexed, tags:"[packagable]\n0\n")).Status
                            == AuctionListingEligibilityStatus.Rejected);
                    foreach (int id in new[] { 27600, 27602, 27673 })
                    {
                        var context = WeaponContext(indexed, id);
                        string before = codec.Serialize(context.Source);
                        var proof = AuctionNativeListingRuleResolver.Resolve(context);
                        Check("M8 native12 factory-and-iteminfo interval " + id + indexed,
                            proof.Kind == AuctionNativeProofKind.VerifiedA21 && proof.Status == AuctionListingEligibilityStatus.Allowed
                            && proof.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount
                            && proof.SourceContentHash == context.SourceContentHash && proof.SourcePvfSha256 == hash
                            && context.Template.HasSingleNativeWeaponDeclaration && context.Template.HasSingleNativeFreeAttachDeclaration);
                        var original = codec.DecodeStrict(context.Source);
                        var transferred = new AuctionTransferableItemAdapter(codec).Transform(context.Source, 1, false);
                        Check("M8 existing cap1 preserves equipment quality99/detail/private " + id + indexed,
                            transferred.NativeCore99.AsSpan().SequenceEqual(context.Source.NativeCore99)
                            && transferred.DetailJson == context.Source.DetailJson && transferred.ExtensionsJson == context.Source.ExtensionsJson
                            && codec.DecodeStrict(transferred).Value == 0x12345678
                            && codec.DecodeStrict(transferred).Upgrade == original.Upgrade && codec.Serialize(context.Source) == before);
                        var request = new AuctionRegisterRequest(0, 65, checked((uint)id), 1, 100, 300, 300, 24, default, default, default);
                        var normalized = AuctionNativeRegistrationAdapter.Normalize(new AuctionParty(79304, 79304, "M8 fixture"),
                            new Guid("812ba2af-0db2-4c2a-9b6a-8be492e27440"), request, original, context.Template);
                        Check("M8 equipmentQ1 reaches existing B7 price branch without stack profile " + id + indexed,
                            normalized.Status == AuctionNativeMutationStatus.Ready && normalized.Request.Quantity == 1
                            && normalized.Request.OpeningBidGold == 100 && normalized.Request.BuyoutGold == 300 && normalized.Request.UnitPriceGold == null);
                        var bidOnly = AuctionNativeRegistrationAdapter.Normalize(new AuctionParty(79304, 79304, "M8 fixture"),
                            new Guid("812ba2af-0db2-4c2a-9b6a-8be492e27440"),
                            request with { Wire16 = uint.MaxValue, Wire20 = 0 }, original, context.Template);
                        Check("M8 equipmentQ1 preserves bid-only sentinel and aggregate " + id + indexed,
                            bidOnly.Status == AuctionNativeMutationStatus.Ready && bidOnly.Request.OpeningBidGold == 100
                            && bidOnly.Request.BuyoutGold == null && bidOnly.Request.UnitPriceGold == null);
                        var fixedPrice = AuctionNativeRegistrationAdapter.Normalize(new AuctionParty(79304, 79304, "M8 fixture"),
                            new Guid("812ba2af-0db2-4c2a-9b6a-8be492e27440"),
                            request with { Wire12 = uint.MaxValue }, original, context.Template);
                        Check("M8 equipmentQ1 buyout-only is whole item not piece " + id + indexed,
                            fixedPrice.Status == AuctionNativeMutationStatus.Ready && fixedPrice.Request.OpeningBidGold == null
                            && fixedPrice.Request.BuyoutGold == 300 && fixedPrice.Request.UnitPriceGold == null);
                        Check("M8 native proof cannot bind another source hash " + id + indexed,
                            AuctionNativeListingRuleResolver.Resolve(context with { SourceContentHash = new string('0',64) }).Kind == AuctionNativeProofKind.Unknown);
                        Check("M8 nonstackQ2 cannot acquire complete proof " + id + indexed,
                            AuctionNativeListingRuleResolver.Resolve(context with { Quantity = 2 }).Status == AuctionListingEligibilityStatus.Rejected);
                    }
                    var weaponNegatives = new (string Name, AuctionNativeListingContext Context)[] {
                        ("Avatar is not native12", WeaponContext(indexed, declaration:"[equipment type]\n`[coat avatar]` 0\n")),
                        ("unknown type cannot use weapon path", WeaponContext(indexed, declaration:"[equipment type]\n`[unknown]` 0\n")),
                        ("duplicate weapon declaration", WeaponContext(indexed, declaration:"[equipment type]\n`[weapon]` 20\n[equipment type]\n`[weapon]` 20\n")),
                        ("missing weapon argument", WeaponContext(indexed, declaration:"[equipment type]\n`[weapon]`\n")),
                        ("equipment type overflow", WeaponContext(indexed, declaration:"[equipment type]\n`[weapon]` 2147483648\n")),
                        ("duplicate attach", WeaponContext(indexed, attach:"[attach type]\n`[free]`\n[attach type]\n`[free]`\n")),
                        ("usable period0", WeaponContext(indexed, tags:"[usable period]\n0\n")),
                        ("expiration0", WeaponContext(indexed, tags:"[expiration date]\n0\n")),
                        ("after unsealing0", WeaponContext(indexed, tags:"[usable period after unsealing]\n0\n")),
                        ("nested after unsealing", WeaponContext(indexed, tags:"[other]\n[usable period after unsealing]\n0\n[/other]\n")),
                        ("uppercase after unsealing missing", WeaponContext(indexed, tags:"[USABLE PERIOD AFTER UNSEALING]\n")),
                        ("malformed after unsealing", WeaponContext(indexed, tags:"[usable period after unsealing]\ninvalid\n")),
                        ("wrong canonical kind", WeaponContext(indexed, kind:ItemCore.KindMaterial))
                    };
                    foreach (var negative in weaponNegatives)
                    {
                        var proof = AuctionNativeListingRuleResolver.Resolve(negative.Context with { HasActiveAuctionPremium = true });
                        Check("M8 " + negative.Name + " remains unknown despite premium " + indexed,
                            proof.Kind == AuctionNativeProofKind.Unknown && proof.Status == AuctionListingEligibilityStatus.Unavailable
                            && proof.Capability == AuctionListingTransferCapability.None);
                    }
                    var blockedWeapon = AuctionNativeListingRuleResolver.Resolve(WeaponContext(indexed, tags:"[packagable]\n0\n")
                        with { HasActiveAuctionPremium = true });
                    Check("M8 packagable0 still rejects before production proof " + indexed,
                        blockedWeapon.Status == AuctionListingEligibilityStatus.Rejected && blockedWeapon.Capability == AuctionListingTransferCapability.None);
                    foreach (var entry in new[] { (3028, "material/piece_leather.stk", 1),
                        (3053, "material/seal_mire.stk", 8), (3156, "material/rubstone_1.stk", 1) })
                    {
                        var context = Context(indexed, entry.Item1, entry.Item2, "[stackable type]\n`[material]` " + entry.Item3 + "\n");
                        var proof = AuctionNativeListingRuleResolver.Resolve(context);
                        Check($"exact supported tuple {entry.Item1} indexed={indexed}", context.Template.SingleMaterialArgument == entry.Item3
                            && context.Template.SubType == -1 && proof.Kind == AuctionNativeProofKind.VerifiedA21
                            && proof.Status == AuctionListingEligibilityStatus.Allowed
                            && proof.Capability == AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount
                            && proof.SourceContentHash == context.SourceContentHash && proof.SourcePvfSha256 == hash
                            && !proof.RequiresPremium && !string.IsNullOrWhiteSpace(proof.EvidenceReference));
                        Check("source hash mismatch cannot grant " + entry.Item1 + indexed,
                            AuctionNativeListingRuleResolver.Resolve(context with { SourceContentHash = new string('0', 64) }).Kind == AuctionNativeProofKind.Unknown);
                    }
                    const string tuple = "[stackable type]\n`[material]` 1\n";
                    var negatives = new (string Name, int Id, string Path, string Declaration, string Tags, string Hash, byte Kind)[] {
                        ("duplicate same tuple", 3028, "material/piece_leather.stk", tuple + tuple, "", hash, ItemCore.KindMaterial),
                        ("duplicate other tuple", 3028, "material/piece_leather.stk", tuple + "[stackable type]\n`[quest]` 1\n", "", hash, ItemCore.KindMaterial),
                        ("missing argument", 3028, "material/piece_leather.stk", "[stackable type]\n`[material]`\n", "", hash, ItemCore.KindMaterial),
                        ("extra argument", 3028, "material/piece_leather.stk", "[stackable type]\n`[material]` 1 0\n", "", hash, ItemCore.KindMaterial),
                        ("overflow argument", 3028, "material/piece_leather.stk", "[stackable type]\n`[material]` 2147483648\n", "", hash, ItemCore.KindMaterial),
                        ("fraction argument", 3028, "material/piece_leather.stk", "[stackable type]\n`[material]` 1.0\n", "", hash, ItemCore.KindMaterial),
                        ("nested tuple", 3028, "material/piece_leather.stk", "[other]\n" + tuple + "[/other]\n", "", hash, ItemCore.KindMaterial),
                        ("case duplicate", 3028, "material/piece_leather.stk", tuple + "[STACKABLE TYPE]\n`[material]` 1\n", "", hash, ItemCore.KindMaterial),
                        ("usable period0", 3028, "material/piece_leather.stk", tuple, "[usable period]\n0\n[auction free]\n", hash, ItemCore.KindMaterial),
                        ("expiration date0", 3028, "material/piece_leather.stk", tuple, "[expiration date]\n0\n[auction free]\n", hash, ItemCore.KindMaterial),
                        ("wrong instance kind", 3028, "material/piece_leather.stk", tuple, "", hash, ItemCore.KindEquipment)
                    };
                    foreach (var test in negatives)
                    {
                        var context = Context(indexed, test.Id, test.Path, test.Declaration, test.Tags, test.Hash, test.Kind);
                        var proof = AuctionNativeListingRuleResolver.Resolve(context with { HasActiveAuctionPremium = true });
                        Check(test.Name + " stays unavailable indexed=" + indexed, proof.Kind == AuctionNativeProofKind.Unknown
                            && proof.Status == AuctionListingEligibilityStatus.Unavailable && proof.Capability == AuctionListingTransferCapability.None);
                    }
                    foreach (bool premium in new[] { false, true })
                    {
                        var context = Context(indexed, 3028, "material/piece_leather.stk", tuple, "[packagable]\n0\n[auction free]\n");
                        var proof = AuctionNativeListingRuleResolver.Resolve(context with { HasActiveAuctionPremium = premium });
                        Check("packagable zero precedes exact support and premium " + indexed + premium,
                            proof.Status == AuctionListingEligibilityStatus.Rejected && proof.Capability == AuctionListingTransferCapability.None);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected native material exception: " + ex); failed++; }
            Console.WriteLine($"AuctionNativeMaterialEligibilitySelfTest: passed={passed} failed={failed}; in-memory only.");
            return failed == 0 ? 0 : 1;
        }
    }
}
