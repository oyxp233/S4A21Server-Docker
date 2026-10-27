using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // Transfer persistence only. These explicit source fixtures do not authorize
    // native auction eligibility for Avatar/Creature or register a runtime entry.
    public static class AuctionDetailTransferSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction detail: {name}"); if (condition) passed++; else failed++; }
            bool CodecRejects(Action action)
            {
                try { action(); return false; }
                catch (AuctionItemCodecException) { return true; }
            }
            bool ArgumentRejects(Action action)
            {
                try { action(); return false; }
                catch (ArgumentException) { return true; }
            }
            const int seller = 78501, buyer = 78502;
            var sourceSession = Guid.NewGuid(); var targetSession = Guid.NewGuid();
            var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-detail-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                int StaticFixture(byte kind, int preferred)
                {
                    if (ItemMetadataResolver.GetEquipmentEntry(preferred) != null
                        && ItemMetadataResolver.TryResolveItemKind(preferred, out var preferredKind) && preferredKind == kind)
                        return preferred;
                    var family = kind == ItemCore.KindAvatar ? "avatar" : "creature";
                    foreach (var entry in ItemMetadataResolver.EquipmentList.Value.Entries
                        .Where(e => (e.FilePath ?? "").IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(e => e.Id))
                        if (ItemMetadataResolver.TryResolveItemKind(entry.Id, out var actualKind) && actualKind == kind)
                            return entry.Id;
                    throw new InvalidOperationException("Current PVF has no real " + family + " template for the actual Existing mail claim fixture.");
                }
                // Existing grant still requires a real static template. The
                // owned core/detail is explicit; no template reconstructs it.
                // Avatar preferred ID is the existing WeaponIllusionSelfTest
                // real weapon-avatar; creature preferred is PetCreatureRuntimeSelfTest.
                int avatarTemplate = StaticFixture(ItemCore.KindAvatar, 101600203);
                int creatureTemplate = StaticFixture(ItemCore.KindCreature, 63000);
                Check("actual mail claim fixtures resolve current PVF Avatar and Creature kinds",
                    ItemMetadataResolver.TryResolveItemKind(avatarTemplate, out var actualAvatarKind) && actualAvatarKind == ItemCore.KindAvatar
                    && ItemMetadataResolver.TryResolveItemKind(creatureTemplate, out var actualCreatureKind) && actualCreatureKind == ItemCore.KindCreature);
                var db = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(78501,'auction-detail-a',''),(78502,'auction-detail-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,level) VALUES(78501,78501,'auction-detail-a',85),(78502,78502,'auction-detail-b',85);");
                var left = InventoryContext.Register(sourceSession, new InventoryService(seller, seller, db));
                var right = InventoryContext.Register(targetSession, new InventoryService(buyer, buyer, db));
                var sockets = new JewelSocket(); sockets.Set(0, 1, 9001); sockets.Set(4, 2, 9005);
                const string opaque = "{\"owner_note\":\"fixture opaque\",\"future\":{\"preserve\":7}}";
                long sourceAvatarUid = 0; int sourceCreatureUid = 0;
                var prepared = OnlineInventoryMutationCommitCoordinator.TryCommit(left, "auction-detail-explicit-owned-fixture", (c, t) => {
                    sourceAvatarUid = AvatarDetailRepository.AllocateAvatarUid(c, t);
                    sourceCreatureUid = checked((int)CreatureDetailRepository.AllocateCreatureUid(c, t));
                    var avatar = new ItemCore { ItemKind = ItemCore.KindAvatar, ItemId = avatarTemplate,
                        AvatarUid = checked((int)sourceAvatarUid), AbilityNo = 321, SealFlag = 1,
                        A21Tail_Unknown84 = 123456, A21Tail_Unknown97 = 987654 };
                    var creature = new ItemCore { ItemKind = ItemCore.KindCreature, ItemId = creatureTemplate,
                        CreatureUid = sourceCreatureUid, SealFlag = 2, Marker16 = 77,
                        A21Tail_Unknown84 = 234567, A21Tail_Unknown97 = 876543 };
                    left.Inventory.AvatarDetails.Attach(new AvatarDetail { AvatarUid = sourceAvatarUid, OwnerId = seller,
                        CharacterId = seller, ItemId = avatarTemplate, ExpireDate = 0, ClearAvatarId = 8765,
                        JewelSocket = sockets.ToBytes(), Color1 = 1234, Color2 = 5678, DeleteDate = 4321 });
                    left.Inventory.AvatarDetails.MarkDirty(sourceAvatarUid);
                    left.Inventory.CreatureDetails.Attach(new CreatureDetail { Uid = sourceCreatureUid,
                        NameBytes = ClientTextEncoding.GetBytes("测试宠物"), Field04 = 83, ModeFlag = 1,
                        Mode1Field0A = 4, Mode1Field0B = 5, ProgressValue32 = 98765, FieldAfterValue32 = 9,
                        ExpireDate = 0, TailFlag = 7, ExtraJson = opaque });
                    left.Inventory.CreatureDetails.MarkDirty(sourceCreatureUid);
                    return left.Inventory.SetItem(InventoryListType.Avatar, 5, avatar)
                        && left.Inventory.SetItem(InventoryListType.Pet, 5, creature);
                });
                Check("explicit owned source core and detail persist on existing transaction", prepared);
                Check("fresh source reload succeeds before codec capture", InventoryRollbackRecoveryService.ReloadOnlineInventory(db.ConnectionString, left));
                Check("source reload preserves all five avatar sockets and creature opaque bytes",
                    left.Inventory.AvatarDetails.GetDetail(sourceAvatarUid).JewelSocket.SequenceEqual(sockets.ToBytes())
                    && left.Inventory.CreatureDetails.GetDetail(sourceCreatureUid).ExtraJson == opaque
                    && left.Inventory.CreatureDetails.GetDetail(sourceCreatureUid).NameBytes.SequenceEqual(ClientTextEncoding.GetBytes("测试宠物")));
                // Existing partial update callers must not erase opaque state if
                // they did not explicitly assign the new ExtraJson field.
                db.Write((c, t) => CreatureDetailRepository.Update(c, t, seller, new CreatureDetail { Uid = sourceCreatureUid,
                    NameBytes = ClientTextEncoding.GetBytes("测试宠物"), Field04 = 83, ModeFlag = 1, Mode1Field0A = 4,
                    Mode1Field0B = 5, ProgressValue32 = 98765, FieldAfterValue32 = 9, TailFlag = 7 }));
                Check("legacy partial creature Update preserves existing opaque JSON", db.Read(c =>
                    CreatureDetailRepository.LoadForCharacter(c, seller)[sourceCreatureUid].ExtraJson) == opaque);
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var transfer = new AuctionTransferableItemAdapter(codec);
                var gate = new object(); var rail = new AuctionPublicAssetCommit(db, codec, gate);
                var mailbox = new MailboxRepository(db);
                AuctionItemEnvelope Capture(InventoryLease owner, InventoryListType list, short slot)
                { lock (owner.SyncRoot) return codec.Capture(owner.Inventory, list, slot); }
                ItemCore WithZeroUid(ItemCore core)
                {
                    var copy = core.Copy();
                    if (copy.ItemKind == ItemCore.KindAvatar) copy.AvatarUid = 0;
                    if (copy.ItemKind == ItemCore.KindCreature) copy.CreatureUid = 0;
                    return copy;
                }
                MailboxSendRequest Mail(Guid operation, int recipient, AuctionItemEnvelope item, byte type)
                    => new() { SenderCharacterId = recipient, SenderAccountId = recipient, SenderName = "fixture system",
                        ReceiverCharacterId = recipient, ReceiverAccountId = recipient, ReceiverName = "fixture recipient",
                        MailType = 1, Text = "fixture strict transferable detail", Unlimited = true,
                        IdempotencyKey = "auction:" + operation.ToString("N") + ":item",
                        Attachments = new[] { new MailboxSendAttachmentRequest { ItemType = type, ItemId = codec.DecodeStrict(item).ItemId,
                            ItemCount = 1, ItemCoreData = item.NativeCore99, DetailJson = item.DetailJson, ExtraJson = item.ExtensionsJson } } };
                AuctionOperationIntent Intent(Guid operation, AuctionPublicAssetPlan plan, int actor)
                    => new(operation, "fixture-detail:" + operation.ToString("N"), AuctionOperationAction.List,
                        actor, actor, 0, 1792700000, JsonSerializer.Serialize(new { publicAssetPlan = plan.Serialize() }));
                long MessageId(AuctionAssetCommitResult outcome)
                {
                    using var doc = JsonDocument.Parse(outcome.Receipt.ResultJson);
                    return doc.RootElement.GetProperty("mailMessageIds")[0].GetInt64();
                }
                void ReplaceDetail(long attachmentId, string detail)
                    => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                        cmd.CommandText = "UPDATE mailbox_attachments SET detail_json=@detail WHERE attachment_id=@id;";
                        cmd.Parameters.AddWithValue("@detail", detail); cmd.Parameters.AddWithValue("@id", attachmentId); cmd.ExecuteNonQuery(); });
                bool ClaimRejected(long claim, InventoryLease owner)
                {
                    try { return !mailbox.ClaimMail(owner.CharacterId, claim, owner).Success; }
                    catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException || ex is JsonException || ex is ArgumentException)
                    { return true; }
                }
                foreach (var kind in new[] { ItemCore.KindAvatar, ItemCore.KindCreature })
                {
                    bool isAvatar = kind == ItemCore.KindAvatar;
                    string label = isAvatar ? "avatar" : "creature";
                    var list = isAvatar ? InventoryListType.Avatar : InventoryListType.Pet;
                    byte mailType = isAvatar ? (byte)1 : (byte)3;
                    int template = isAvatar ? avatarTemplate : creatureTemplate;
                    long originalUid = isAvatar ? sourceAvatarUid : sourceCreatureUid;
                    string sourceTable = isAvatar ? "character_avatar_detail" : "character_creatures";
                    string uidColumn = isAvatar ? "item_uid" : "creature_key";
                    var source = Capture(left, list, 5);
                    var durable = codec.Serialize(source);
                    var restored = codec.DeserializeStrict(durable);
                    var final = transfer.Transform(restored, 1, returning: false);
                    var finalCore = codec.DecodeStrict(final);
                    Check(label + " strict source survives stored-envelope roundtrip", codec.Serialize(restored) == durable && source.DetailJson.Length > 0);
                    Check(label + " transfer changes only documented UID and keeps detail/private state",
                        WithZeroUid(codec.DecodeStrict(source)).ToBytes().SequenceEqual(final.NativeCore99)
                        && final.DetailJson == source.DetailJson && final.ExtensionsJson == source.ExtensionsJson
                        && (isAvatar ? finalCore.AvatarUid == 0 : finalCore.CreatureUid == 0));
                    Check(label + " return derives the same owner-neutral instance", transfer.Transform(source, 1, returning: true).ContentHashHex == final.ContentHashHex);
                    Check(label + " detail cannot be omitted even with a newly computed outer hash", CodecRejects(() =>
                        codec.DecodeStrict(AuctionItemEnvelope.Create(final.NativeCore99, "", final.ExtensionsJson))));
                    Check(label + " detail duplicate version rejected despite valid outer hash", CodecRejects(() =>
                        codec.DecodeStrict(AuctionItemEnvelope.Create(final.NativeCore99, final.DetailJson.Insert(1, "\"FormatVersion\":1,"), final.ExtensionsJson))));
                    Check(label + " cannot be split into multiple instances", ArgumentRejects(() => transfer.Transform(source, 2, false)));
                    var missing = new InventoryService(seller, seller); missing.SetItem(list, 5, codec.DecodeStrict(source));
                    Check(label + " absent actual source detail cannot be template-created", CodecRejects(() => codec.Capture(missing, list, 5)));
                    if (isAvatar)
                    {
                        var detail = left.Inventory.AvatarDetails.GetDetail(originalUid);
                        int oldOwner = detail.OwnerId; detail.OwnerId = buyer;
                        Check("avatar actual source ownership mismatch rejected", CodecRejects(() => Capture(left, list, 5)));
                        detail.OwnerId = oldOwner;
                        ushort color = detail.Color1; detail.Color1++;
                        Check("same99B changed avatar detail invalidates source snapshot", !codec.MatchesSource(left.Inventory, list, 5, source));
                        detail.Color1 = color;
                    }
                    else
                    {
                        var detail = left.Inventory.CreatureDetails.GetDetail(checked((int)originalUid));
                        string old = detail.ExtraJson; detail.ExtraJson = "{\"changed\":true}";
                        Check("same99B changed opaque creature state invalidates source snapshot", !codec.MatchesSource(left.Inventory, list, 5, source));
                        detail.ExtraJson = old; detail.ExpireDate = 1900000000;
                        Check("nonpersistable timed creature detail remains rejected", CodecRejects(() => Capture(left, list, 5)));
                        detail.ExpireDate = 0;
                    }

                    var failedId = Guid.NewGuid();
                    var failedPlan = AuctionPublicAssetPlan.Create(codec, 0,
                        new[] { new AuctionItemDebit(list, 5, 1, source) }, new[] { Mail(failedId, buyer, final, mailType) });
                    var failedIntent = Intent(failedId, failedPlan, seller);
                    bool observedConsumption = false;
                    var faultRail = new AuctionPublicAssetCommit(db, codec, gate, point => {
                        if (point != AuctionAssetFaultPoint.BeforePublicCommit) return;
                        observedConsumption = left.Inventory.GetItem(list, 5) == null && (isAvatar
                            ? !left.Inventory.AvatarDetails.TryGetDetail(originalUid, out _)
                            : !left.Inventory.CreatureDetails.TryGetDetail(checked((int)originalUid), out _));
                        throw new InvalidOperationException("fixture detail transaction rollback");
                    });
                    var failedOutcome = faultRail.Execute(failedIntent, failedPlan, left);
                    Check(label + " source consume happened in real failing public transaction", observedConsumption);
                    Check(label + " public rollback restores source core/detail and leaves no receipt", failedOutcome.State == AuctionAssetCommitState.Rejected
                        && Capture(left, list, 5).ContentHashHex == source.ContentHashHex
                        && Scalar("SELECT count(*) FROM " + sourceTable + " WHERE " + uidColumn + "=" + originalUid + ";") == 1
                        && db.Read(c => AuctionPublicReceiptRepository.Find(c, null, failedIntent)) == null);
                    var operation = Guid.NewGuid();
                    var plan = AuctionPublicAssetPlan.Create(codec, 0,
                        new[] { new AuctionItemDebit(list, 5, 1, source) }, new[] { Mail(operation, buyer, final, mailType) });
                    var committed = rail.Execute(Intent(operation, plan, seller), plan, left);
                    Check(label + " real source-to-mail commit removes source core and its detail together", committed.State == AuctionAssetCommitState.Committed
                        && left.Inventory.GetItem(list, 5) == null
                        && Scalar("SELECT count(*) FROM " + sourceTable + " WHERE " + uidColumn + "=" + originalUid + ";") == 0);
                    long message = MessageId(committed);
                    var attachment = mailbox.LoadInbox(buyer, 100).Single(x => x.MessageId == message).Attachments.Single();
                    long claim = MailboxRepository.AttachmentClaimFlag + attachment.AttachmentId;
                    Check(label + " persisted mail exactly matches frozen final core/detail/extensions", attachment.ItemCoreData.SequenceEqual(final.NativeCore99)
                        && attachment.DetailJson == final.DetailJson && attachment.ExtraJson == final.ExtensionsJson && attachment.ItemCount == 1);
                    ReplaceDetail(attachment.AttachmentId, "");
                    Check(label + " damaged marker cannot downgrade auction-origin attachment to legacy", ClaimRejected(claim, right)
                        && Scalar("SELECT claimed_flag FROM mailbox_attachments WHERE attachment_id=" + attachment.AttachmentId + ";") == 0
                        && !right.Inventory.GetItems(list).Any(x => x.Value.ItemId == template));
                    ReplaceDetail(attachment.AttachmentId, final.DetailJson.Insert(1, "\"FormatVersion\":1,"));
                    Check(label + " actual claim rejects corrupt strict payload without a default grant", ClaimRejected(claim, right)
                        && Scalar("SELECT claimed_flag FROM mailbox_attachments WHERE attachment_id=" + attachment.AttachmentId + ";") == 0);
                    ReplaceDetail(attachment.AttachmentId, final.DetailJson);
                    Sql("UPDATE mailbox_attachments SET item_count=2 WHERE attachment_id=" + attachment.AttachmentId + ";");
                    Check(label + " actual claim rejects inconsistent redundant nonstackable count", ClaimRejected(claim, right)
                        && Scalar("SELECT claimed_flag FROM mailbox_attachments WHERE attachment_id=" + attachment.AttachmentId + ";") == 0);
                    Sql("UPDATE mailbox_attachments SET item_count=1 WHERE attachment_id=" + attachment.AttachmentId + ";");
                    Sql("CREATE TRIGGER auction_detail_claim_failure BEFORE INSERT ON character_inventory_items WHEN NEW.character_id=78502 "
                        + "AND NEW.list_type=" + (int)list + " BEGIN SELECT RAISE(ABORT,'fixture auction claim after uid allocation'); END;");
                    bool reachedClaimSave = false;
                    try
                    {
                        var unexpectedResult = mailbox.ClaimMail(buyer, claim, right);
                        Console.WriteLine("[DIAG] " + label + " claim returned before expected save trigger: success="
                            + unexpectedResult.Success + " error=" + unexpectedResult.Error);
                    }
                    catch (SqliteException ex) { reachedClaimSave = ex.Message.Contains("fixture auction claim after uid allocation", StringComparison.Ordinal); }
                    finally { Sql("DROP TRIGGER auction_detail_claim_failure;"); }
                    Check(label + " receiver failure really occurs after UID allocation at ordinary save", reachedClaimSave);
                    Check(label + " failed claim leaves attachment unclaimed and no receiver core/detail", Scalar("SELECT claimed_flag FROM mailbox_attachments WHERE attachment_id=" + attachment.AttachmentId + ";") == 0
                        && !right.Inventory.GetItems(list).Any(x => x.Value.ItemId == template)
                        && Scalar("SELECT count(*) FROM " + sourceTable + " WHERE character_id=78502;") == 0);
                    var claimed = mailbox.ClaimMail(buyer, claim, right);
                    Check(label + " real mailbox claim succeeds once after transient save failure (" + claimed.Error + ")", claimed.Success && claimed.ClaimedAttachmentCount == 1);
                    Check(label + " receiver authoritative reload succeeds", InventoryRollbackRecoveryService.ReloadOnlineInventory(db.ConnectionString, right));
                    var receivedSlot = right.Inventory.GetItems(list).Single(x => x.Value.ItemId == template);
                    var received = Capture(right, list, receivedSlot.Key);
                    var receivedCore = codec.DecodeStrict(received);
                    long recipientUid = isAvatar ? receivedCore.AvatarUid : receivedCore.CreatureUid;
                    Check(label + " receiver has fresh UID but exact transferable core/detail/private state", recipientUid > 0 && recipientUid != originalUid
                        && WithZeroUid(receivedCore).ToBytes().SequenceEqual(final.NativeCore99)
                        && received.DetailJson == source.DetailJson && received.ExtensionsJson == source.ExtensionsJson);
                    if (isAvatar)
                    {
                        var detail = right.Inventory.AvatarDetails.GetDetail(recipientUid);
                        Check("avatar receiver owner and all30B including fifth socket survive reload", detail.OwnerId == buyer && detail.CharacterId == buyer
                            && detail.ItemId == template && detail.JewelSocket.SequenceEqual(sockets.ToBytes())
                            && detail.Color1 == 1234 && detail.Color2 == 5678 && detail.ClearAvatarId == 8765 && detail.DeleteDate == 4321
                            && receivedCore.AbilityNo == 321);
                    }
                    else
                    {
                        var detail = right.Inventory.CreatureDetails.GetDetail(checked((int)recipientUid));
                        Check("creature type GBK name progress opaque state survive receiver reload", receivedCore.SealFlag == 2
                            && detail.ExtraJson == opaque && detail.NameBytes.SequenceEqual(ClientTextEncoding.GetBytes("测试宠物"))
                            && detail.Field04 == 83 && detail.ModeFlag == 1 && detail.Mode1Field0A == 4 && detail.Mode1Field0B == 5
                            && detail.ProgressValue32 == 98765 && detail.FieldAfterValue32 == 9 && detail.TailFlag == 7);
                    }
                    Check(label + " duplicate actual claim never creates a second instance", !mailbox.ClaimMail(buyer, claim, right).Success
                        && right.Inventory.GetItems(list).Count(x => x.Value.ItemId == template) == 1);
                    var returnId = Guid.NewGuid(); var returnedFinal = transfer.Transform(received, 1, returning: true);
                    var returnPlan = AuctionPublicAssetPlan.Create(codec, 0,
                        new[] { new AuctionItemDebit(list, receivedSlot.Key, 1, received) }, new[] { Mail(returnId, seller, returnedFinal, mailType) });
                    var returned = rail.Execute(Intent(returnId, returnPlan, buyer), returnPlan, right);
                    Check(label + " return commits through same source/mail rail", returned.State == AuctionAssetCommitState.Committed);
                    var returnAttachment = mailbox.LoadInbox(seller, 100).Single(x => x.MessageId == MessageId(returned)).Attachments.Single();
                    var returnClaimed = mailbox.ClaimMail(seller, MailboxRepository.AttachmentClaimFlag + returnAttachment.AttachmentId, left);
                    Check(label + " seller claims actual return and authoritative reload", returnClaimed.Success
                        && InventoryRollbackRecoveryService.ReloadOnlineInventory(db.ConnectionString, left));
                    var returnedSlot = left.Inventory.GetItems(list).Single(x => x.Value.ItemId == template);
                    var returnedItem = Capture(left, list, returnedSlot.Key);
                    var returnedCore = codec.DecodeStrict(returnedItem);
                    long returnedUid = isAvatar ? returnedCore.AvatarUid : returnedCore.CreatureUid;
                    Check(label + " returned state conserves one instance without resurrecting source UID", returnedUid > 0
                        && returnedUid != originalUid && returnedUid != recipientUid
                        && returnedItem.DetailJson == source.DetailJson && WithZeroUid(returnedCore).ToBytes().SequenceEqual(final.NativeCore99)
                        && !right.Inventory.GetItems(list).Any(x => x.Value.ItemId == template)
                        && Scalar("SELECT count(*) FROM " + sourceTable + " WHERE character_id IN(78501,78502);") == 1);
                }
                // M7 draft: appended after all existing transfer assertions.
                // Uses the same real SQLite fixture/leases/actual PVF Avatar.
                // This is an explicit frozen-contract fixture, not native eligibility.
                {
                    const long at = 1792700100;
                    var sellerParty = new AuctionParty(seller, seller, "m7 fixture seller");
                    var buyerParty = new AuctionParty(buyer, buyer, "m7 fixture buyer");
                    string privatePath = Path.Combine(root, "m7-avatar-private.db");
                    var orderStore = new AuctionOrderStore(privatePath, codec);
                    var recoveryPlanner = new AuctionMarketPlanner(codec, transfer.Transform);
                    var approvedQuote = new AuctionApprovedQuoteProvider((r, b, n) => new(null, 0, false));
                    AuctionVerifiedQuote Quote(AuctionMarketRequest request, AuctionOrderSnapshot before, long n)
                        => approvedQuote.Quote(request, before, n);
                    var frozenWriter = typeof(AuctionMarketApplication).GetMethod("FreezeEligibility",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        ?? throw new InvalidOperationException("Missing actual frozen eligibility writer.");
                    var authorize = typeof(AuctionMarketApplication).GetMethod("AuthorizedListing",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?? throw new InvalidOperationException("Missing actual fixture authority gate.");
                    int unexpectedReads = 0;
                    AuctionVerifiedQuote NeverQuote(AuctionMarketRequest r, AuctionOrderSnapshot b, long n)
                    { unexpectedReads++; throw new InvalidOperationException("Recovery must use frozen quote."); }
                    AuctionMarketApplication ResumeOnly(AuctionOrderStore store, AuctionPublicAssetCommit publicRail, bool allow)
                        => new(store, codec, recoveryPlanner, null, NeverQuote, publicRail,
                            (_, _) => { unexpectedReads++; throw new InvalidOperationException("Recovery must use frozen mail."); },
                            gate, () => at, allowFixtureListingProofs: allow,
                            deferredListingEligibility: () => { unexpectedReads++; throw new InvalidOperationException("Recovery must not Evaluate."); });
                    AuctionOperationIntent Frozen(Guid id, AuctionOrderTransition transition, AuctionPublicAssetPlan plan,
                        AuctionParty actor, string eligibility)
                        => new(id, "order:" + transition.After.OrderId, transition.Quote.Binding.Action,
                            actor.CharacterId, actor.AccountId, transition.Quote.Binding.ExpectedVersion, at,
                            JsonSerializer.Serialize(new { publicAssetPlan = plan.Serialize(), orderTransition = transition.Serialize(),
                                listingEligibility = eligibility }));
                    MailboxSendRequest LegMail(AuctionDeliveryLeg leg, Guid id)
                    {
                        var mail = leg.ItemEnvelopeJson != null
                            ? Mail(id, leg.Recipient.CharacterId, codec.DeserializeStrict(leg.ItemEnvelopeJson), 1)
                            : new MailboxSendRequest { SenderCharacterId = leg.Recipient.CharacterId,
                                SenderAccountId = leg.Recipient.AccountId, SenderName = "m7 fixture system",
                                ReceiverCharacterId = leg.Recipient.CharacterId, ReceiverAccountId = leg.Recipient.AccountId,
                                ReceiverName = leg.Recipient.FrozenName, MailType = 1, Unlimited = true, Text = "m7 frozen liability" };
                        mail.Gold = checked((int)leg.Gold);
                        mail.IdempotencyKey = "auction:" + id.ToString("N") + ":" + leg.LegKey;
                        return mail;
                    }
                    bool IdentityConflict(Action action)
                    { try { action(); return false; } catch (InvalidOperationException) { return true; } }
                    long ReceiptCount(Guid id) => Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='auction_asset:"
                        + id.ToString("N") + "';");
                    long OperationMailCount(Guid id) => Scalar("SELECT count(*) FROM mailbox_messages WHERE idempotency_key LIKE 'auction:"
                        + id.ToString("N") + ":%';");
                    Check("M7 capability appends2 without renumbering old values",
                        (int)AuctionListingTransferCapability.None == 0
                        && (int)AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount == 1
                        && (int)AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased == 2);
                    Check("M7 explicit fixture wallets persist on existing owner",
                        OnlineInventoryMutationCommitCoordinator.TryCommit(left, "m7-avatar-fixture-wallet", (c, t) => left.Inventory.SetMainVirtualCount(0, 30000))
                        && OnlineInventoryMutationCommitCoordinator.TryCommit(right, "m7-avatar-fixture-wallet", (c, t) => right.Inventory.SetMainVirtualCount(0, 5000)));
                    foreach (bool publicCommitted in new[] { false, true })
                    {
                        short slot = publicCommitted ? (short)7 : (short)6;
                        long sourceUid = 0;
                        Check("M7 permanent Source1 fixture persists at slot" + slot,
                            OnlineInventoryMutationCommitCoordinator.TryCommit(left, "m7-avatar-permanent-fixture", (c, t) => {
                                var occupied = left.Inventory.GetItem(InventoryListType.Avatar, slot);
                                if (occupied != null && !occupied.IsEmpty) return false;
                                sourceUid = AvatarDetailRepository.AllocateAvatarUid(c, t);
                                left.Inventory.AvatarDetails.Attach(new AvatarDetail { AvatarUid = sourceUid, OwnerId = seller,
                                    CharacterId = seller, ItemId = avatarTemplate, ExpireDate = 0, ClearAvatarId = 0,
                                    JewelSocket = sockets.ToBytes(), Color1 = 1234, Color2 = 5678, DeleteDate = 0 });
                                left.Inventory.AvatarDetails.MarkDirty(sourceUid);
                                return left.Inventory.SetItem(InventoryListType.Avatar, slot,
                                    new ItemCore { ItemKind = ItemCore.KindAvatar, ItemId = avatarTemplate,
                                        AvatarUid = checked((int)sourceUid), AbilityNo = 321, SealFlag = 0,
                                        A21Tail_Unknown84 = 123456, A21Tail_Unknown97 = 987654 });
                            }));
                        Check("M7 source authoritative reload", InventoryRollbackRecoveryService.ReloadOnlineInventory(db.ConnectionString, left));
                        var owned = Capture(left, InventoryListType.Avatar, slot);
                        long walletBefore = left.Inventory.GetMainVirtualCount(0).Count;
                        var nativeProof = new AuctionNativeListingProof(AuctionNativeProofKind.Fixture,
                            AuctionListingEligibilityStatus.Allowed, avatarTemplate, owned.ContentHashHex,
                            PvfArchiveAccessor.SourceSha256, AuctionListingTransferCapability.PermanentOrdinaryAvatarUidRebased,
                            false, "explicit-test-frozen-avatar-contract", "fixture only; production resolver unchanged");
                        var eligibility = new AuctionListingEligibilityResult(AuctionListingEligibilityStatus.Allowed,
                            "explicit test fixture", left.SessionId, seller, seller, InventoryListType.Avatar, slot, 1, at,
                            PvfArchiveAccessor.SourceSha256, owned, nativeProof);
                        var fixtureApp = ResumeOnly(orderStore, rail, true);
                        Check("M7 fixture cap2 requires explicit allowFixture authority",
                            authorize.Invoke(fixtureApp, new object[] { eligibility }) is true
                            && authorize.Invoke(ResumeOnly(orderStore, rail, false), new object[] { eligibility }) is false);
                        Check("M7 Avatar cap2 cannot authorize Main0 source",
                            authorize.Invoke(fixtureApp, new object[] { eligibility with { List = InventoryListType.Main } }) is false);
                        Check("M7 Avatar cap2 cannot authorize equipped Equipment3 source",
                            authorize.Invoke(fixtureApp, new object[] { eligibility with { List = InventoryListType.Equipment } }) is false);
                        Check("M7 Avatar cap2 cannot authorize quantity2",
                            authorize.Invoke(fixtureApp, new object[] { eligibility with { Quantity = 2 } }) is false);
                        Check("M7 Main cap1 cannot authorize Source1 KindAvatar",
                            authorize.Invoke(fixtureApp, new object[] { eligibility with { NativeProof = nativeProof with {
                                Capability = AuctionListingTransferCapability.PermanentOrdinaryMainOnlyCount } } }) is false);
                        string frozenEligibility = frozenWriter.Invoke(null,
                            new object[] { eligibility, new AuctionEntitlementSnapshot(seller, null, at) }) as string
                            ?? throw new InvalidOperationException("Actual frozen writer returned no string.");
                        var request = new AuctionMarketRequest(Guid.NewGuid(), AuctionOperationAction.List,
                            orderStore.AllocateOrderId(), 0, sellerParty, 1, 0, at + 86400, BuyoutGold: 1000,
                            SourceList: InventoryListType.Avatar, SourceSlot: slot);
                        var listing = recoveryPlanner.Plan(request, null, owned, Quote(request, null, at), at);
                        Check("M7 nonstack listing keeps source1 quantity1 and neutral UID0",
                            listing.Status == AuctionMarketStatus.Planned && listing.Plan.Transition.After.RemainingQuantity == 1
                            && codec.DecodeStrict(codec.DeserializeStrict(listing.Plan.Transition.After.SourceEnvelopeJson)).AvatarUid == sourceUid
                            && codec.DecodeStrict(codec.DeserializeStrict(listing.Plan.Transition.After.EscrowEnvelopeJson)).AvatarUid == 0);
                        var listPlan = AuctionPublicAssetPlan.Create(codec, listing.Plan.GoldDebit,
                            new[] { new AuctionItemDebit(InventoryListType.Avatar, slot, 1, owned) }, Array.Empty<MailboxSendRequest>());
                        var intent = Frozen(request.OperationId, listing.Plan.Transition, listPlan, sellerParty, frozenEligibility);
                        using (var outer = JsonDocument.Parse(intent.IntentJson))
                        using (var frozenProof = JsonDocument.Parse(outer.RootElement.GetProperty("listingEligibility").GetString()))
                            Check("M7 actual FreezeEligibility writer saves cap2 as hash-bound extra string",
                                frozenProof.RootElement.GetProperty("NativeProof").GetProperty("Capability").GetInt32() == 2
                                && frozenProof.RootElement.GetProperty("List").GetInt32() == 1
                                && frozenProof.RootElement.GetProperty("Slot").GetInt32() == slot);
                        var wrongCore = codec.DecodeStrict(codec.DeserializeStrict(listing.Plan.Transition.After.EscrowEnvelopeJson));
                        wrongCore.AvatarUid = 1; // Count aliases UID; never replace neutral UID with quantity1.
                        var wrongAfter = listing.Plan.Transition.After with { EscrowEnvelopeJson = codec.Serialize(codec.CreateTransferSnapshot(owned, wrongCore)) };
                        var wrongTransition = new AuctionOrderTransition(null, wrongAfter, listing.Plan.Transition.Quote, Array.Empty<AuctionDeliveryLeg>());
                        var wrongIntent = Frozen(Guid.NewGuid(), wrongTransition, listPlan, sellerParty, frozenEligibility);
                        Check("M7 Store refuses fake Count1 in Avatar escrow before reserving",
                            ArgumentRejects(() => orderStore.Prepare(wrongIntent, wrongTransition))
                            && orderStore.FindOrder(request.OrderId) == null);
                        orderStore.Prepare(intent, listing.Plan.Transition);
                        var journal = orderStore.FindOperation(intent.OperationId);
                        string changedEligibility = frozenEligibility.Replace("\"Capability\":2", "\"Capability\":1", StringComparison.Ordinal);
                        var changed = Frozen(intent.OperationId, listing.Plan.Transition, listPlan, sellerParty, changedEligibility);
                        Check("M7 unknown frozen extra stays byte exact; changed JSON is identity conflict",
                            journal.Intent.IntentJson == intent.IntentJson && journal.Intent.IntentHash == intent.IntentHash
                            && changedEligibility != frozenEligibility && changed.IntentHash != intent.IntentHash
                            && IdentityConflict(() => journal.Intent.RequireSame(changed)));
                        Check("M7 Source1 plan canonical reader retains positive owned UID",
                            AuctionPublicAssetPlan.DeserializeStrict(codec, listPlan.Serialize()).ItemDebits.Single().List == InventoryListType.Avatar
                            && codec.DecodeStrict(AuctionPublicAssetPlan.DeserializeStrict(codec, listPlan.Serialize()).ItemDebits.Single().Source).AvatarUid == sourceUid);
                        if (!publicCommitted)
                            Check("M7 Resume with absent receipt is pending and never retries Source1 debit",
                                fixtureApp.Resume(intent.OperationId).Status == AuctionMarketStatus.Pending
                                && ReceiptCount(intent.OperationId) == 0 && Capture(left, InventoryListType.Avatar, slot).ContentHashHex == owned.ContentHashHex);
                        else
                        {
                            var publicResult = rail.Execute(intent, listPlan, left);
                            Check("M7 public receipt exists before private completion",
                                publicResult.State == AuctionAssetCommitState.Committed && ReceiptCount(intent.OperationId) == 1
                                && orderStore.FindOrder(request.OrderId).State == AuctionOrderState.Funding
                                && left.Inventory.GetItem(InventoryListType.Avatar, slot) == null
                                && left.Inventory.GetMainVirtualCount(0).Count == walletBefore - 10000
                                && rail.ReadOutcome(changed).State == AuctionAssetCommitState.Conflict
                                && Scalar("SELECT count(*) FROM character_avatar_detail WHERE item_uid=" + sourceUid + ";") == 0);
                        }
                        orderStore = new AuctionOrderStore(privatePath, codec);
                        var restartedRail = new AuctionPublicAssetCommit(db, codec, gate);
                        var recovery = new AuctionOrderRecovery(orderStore, db, restartedRail, gate);
                        var result = recovery.RecoverPendingAfterExecutorsQuiesced(10);
                        Check("M7 quiescent recovery resolves the exact public boundary " + publicCommitted,
                            result.Completed == (publicCommitted ? 1 : 0) && result.Aborted == (publicCommitted ? 0 : 1)
                            && result.Pending == 0 && result.Quarantined == 0 && orderStore.Pending(10).Count == 0);
                        var terminalApp = ResumeOnly(orderStore, restartedRail, true);
                        Check("M7 repeated quiescent recovery and terminal Resume are idempotent",
                            recovery.RecoverPendingAfterExecutorsQuiesced(10) == new AuctionOrderRecoveryResult(0, 0, 0, 0)
                            && terminalApp.Resume(intent.OperationId).Status == (publicCommitted ? AuctionMarketStatus.Committed : AuctionMarketStatus.Aborted)
                            && terminalApp.Resume(intent.OperationId).Operation.Intent.IntentHash == intent.IntentHash
                            && ReceiptCount(intent.OperationId) == (publicCommitted ? 1 : 0)
                            && left.Inventory.GetMainVirtualCount(0).Count == walletBefore - (publicCommitted ? 10000 : 0)
                            && OperationMailCount(intent.OperationId) == 0 && unexpectedReads == 0);
                        if (!publicCommitted)
                        {
                            Check("M7 abort leaves exact owned core/detail/private and wallet untouched",
                                orderStore.FindOrder(request.OrderId) == null && InventoryRollbackRecoveryService.ReloadOnlineInventory(db.ConnectionString, left)
                                && Capture(left, InventoryListType.Avatar, slot).ContentHashHex == owned.ContentHashHex
                                && left.Inventory.GetMainVirtualCount(0).Count == walletBefore);
                            continue;
                        }
                        var active = orderStore.FindOrder(request.OrderId);
                        Check("M7 recovered escrow preserves original UID source and full permanent detail",
                            active.SourceEnvelopeJson == codec.Serialize(owned) && active.RemainingQuantity == 1
                            && codec.DecodeStrict(codec.DeserializeStrict(active.EscrowEnvelopeJson)).AvatarUid == 0
                            && codec.DeserializeStrict(active.EscrowEnvelopeJson).DetailJson == owned.DetailJson
                            && owned.ExtensionsJson == "{}"); // Never forge reforge metadata on KindAvatar.
                        var buy = new AuctionMarketRequest(Guid.NewGuid(), AuctionOperationAction.Buyout,
                            active.OrderId, active.Version, buyerParty, 1, 1000, 0);
                        var sale = recoveryPlanner.Plan(buy, active, null, Quote(buy, active, at + 1), at + 1);
                        var salePlan = AuctionPublicAssetPlan.Create(codec, sale.Plan.GoldDebit, Array.Empty<AuctionItemDebit>(),
                            sale.Plan.Transition.Legs.Select(leg => LegMail(leg, buy.OperationId)).ToArray());
                        var saleIntent = Frozen(buy.OperationId, sale.Plan.Transition, salePlan, buyerParty, null);
                        orderStore.Prepare(saleIntent, sale.Plan.Transition);
                        var paid = restartedRail.Execute(saleIntent, salePlan, right);
                        Check("M7 recovered UID0 Avatar is funded once through actual sale/mail rail",
                            paid.State == AuctionAssetCommitState.Committed && right.Inventory.GetMainVirtualCount(0).Count == 4000);
                        Check("M7 receipt-backed sale recovery then repeat preserves mail ids",
                            recovery.RecoverPendingAfterExecutorsQuiesced(10).Completed == 1
                            && terminalApp.Resume(saleIntent.OperationId).Status == AuctionMarketStatus.Committed
                            && recovery.RecoverPendingAfterExecutorsQuiesced(10) == new AuctionOrderRecoveryResult(0, 0, 0, 0)
                            && orderStore.FindOrder(active.OrderId).State == AuctionOrderState.Sold && ReceiptCount(buy.OperationId) == 1);
                        var attachment = mailbox.LoadInbox(buyer, 100).Single(m => m.MessageId == MessageId(paid)).Attachments.Single();
                        Check("M7 delivered attachment keeps quantity1 with AvatarUid0 and exact detail",
                            attachment.ItemCount == 1 && ItemCore.FromBytes(attachment.ItemCoreData).AvatarUid == 0
                            && attachment.DetailJson == owned.DetailJson && attachment.ExtraJson == "{}");
                        long claimKey = MailboxRepository.AttachmentClaimFlag + attachment.AttachmentId;
                        var claim = mailbox.ClaimMail(buyer, claimKey, right);
                        Check("M7 actual claim allocates recipient UID on its existing transaction", claim.Success
                            && claim.ClaimedAttachmentCount == 1 && InventoryRollbackRecoveryService.ReloadOnlineInventory(db.ConnectionString, right));
                        var receivedSlot = right.Inventory.GetItems(InventoryListType.Avatar).Single(p => p.Value.ItemId == avatarTemplate);
                        var received = Capture(right, InventoryListType.Avatar, receivedSlot.Key);
                        var receivedCore = codec.DecodeStrict(received);
                        var neutral = codec.DeserializeStrict(active.EscrowEnvelopeJson);
                        var receivedDetail = right.Inventory.AvatarDetails.GetDetail(receivedCore.AvatarUid);
                        Check("M7 newUID preserves full99 except UID, nonzero AbilityNo and all30B/colors",
                            receivedCore.AvatarUid > 0 && receivedCore.AvatarUid != sourceUid && receivedCore.AbilityNo == 321
                            && WithZeroUid(receivedCore).ToBytes().SequenceEqual(neutral.NativeCore99)
                            && received.DetailJson == owned.DetailJson && received.ExtensionsJson == "{}"
                            && receivedDetail.OwnerId == buyer && receivedDetail.CharacterId == buyer
                            && receivedDetail.JewelSocket.SequenceEqual(sockets.ToBytes()) && receivedDetail.Color1 == 1234 && receivedDetail.Color2 == 5678);
                        Check("M7 duplicate claim and terminal Resume cannot duplicate UID/item/receipt",
                            !mailbox.ClaimMail(buyer, claimKey, right).Success
                            && right.Inventory.GetItems(InventoryListType.Avatar).Count(p => p.Value.ItemId == avatarTemplate) == 1
                            && terminalApp.Resume(buy.OperationId).Status == AuctionMarketStatus.Committed
                            && right.Inventory.GetMainVirtualCount(0).Count == 4000
                            && ReceiptCount(buy.OperationId) == 1 && OperationMailCount(buy.OperationId) == 3
                            && orderStore.FindOperation(buy.OperationId).ResultJson == paid.Receipt.ResultJson && unexpectedReads == 0);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] unexpected auction detail transfer exception: " + ex); failed++; }
            finally
            {
                InventoryContext.Unregister(sourceSession); InventoryContext.Unregister(targetSession);
                SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
            }
            Console.WriteLine($"AuctionDetailTransferSelfTest: passed={passed} failed={failed}; transport only, native listing eligibility unchanged.");
            return failed == 0 ? 0 : 1;
        }
    }
}
