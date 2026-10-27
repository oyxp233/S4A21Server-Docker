using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Storage
{
    // All mutations compose the order and its journal in one private transaction.
    // Public effects are already durable before Complete; these databases are never treated as atomic.
    internal sealed partial class AuctionOrderStore
    {
        private readonly IGameDatabase database;
        private readonly AuctionItemCodec codec;
        private readonly bool allowFixturePolicies;
        internal AuctionOrderStore(string privatePath, AuctionItemCodec codec, bool allowFixturePolicies = false)
        {
            database = AuctionDatabase.Open(privatePath);
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.allowFixturePolicies = allowFixturePolicies;
        }

        internal long AllocateOrderId() => database.Write((c, t) => {
            using var cmd = Command(c, t, @"UPDATE auction_market_sequence SET next_id=next_id+1
WHERE singleton=1 AND next_id<9223372036854775807 RETURNING next_id-1;");
            return Convert.ToInt64(cmd.ExecuteScalar() ?? throw new InvalidOperationException("Auction IDs exhausted."));
        });
        internal AuctionOrderSnapshot FindOrder(long id) => database.Read(c => Find(c, null, id));
        internal AuctionOrderReadSnapshot ReadQueryCandidates(AuctionOrderReadScope scope, int accountId,
            int characterId, long observedAtUnix, int maximumSnapshotRows = 5000)
        {
            if (!Enum.IsDefined(scope) || accountId <= 0 || characterId <= 0 || observedAtUnix <= 0
                || maximumSnapshotRows < 1 || maximumSnapshotRows > 5000)
                throw new ArgumentException("Invalid authenticated auction read scope/budget.");
            return database.Read(c => {
                // Every ID and its validated row belong to the same SQLite
                // read snapshot. No public database or second writer is held.
                using var t = c.BeginTransaction(deferred: true);
                var predicate = scope switch {
                    AuctionOrderReadScope.PublicActive => "",
                    AuctionOrderReadScope.SellerActive => " AND seller_character_id=@cid AND seller_account_id=@aid",
                    AuctionOrderReadScope.CurrentHighestBidderActive => " AND bidder_character_id=@cid",
                    _ => throw new ArgumentOutOfRangeException(nameof(scope))
                };
                using var cmd = Command(c, t, @"SELECT order_id FROM auction_order WHERE state=1
AND pending_operation_id IS NULL AND expires_at_unix>@now" + predicate + " ORDER BY order_id LIMIT @limit;");
                Add(cmd, "@cid", characterId); Add(cmd, "@aid", accountId); Add(cmd, "@now", observedAtUnix);
                Add(cmd, "@limit", maximumSnapshotRows + 1);
                var ids = new List<long>();
                using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
                if (ids.Count > maximumSnapshotRows)
                    return new AuctionOrderReadSnapshot(false, "SnapshotRowBudgetExceeded", observedAtUnix, Array.Empty<AuctionOrderSnapshot>());
                var rows = new List<AuctionOrderSnapshot>(ids.Count);
                foreach (long id in ids)
                {
                    var row = Find(c, t, id) ?? throw new InvalidOperationException("Auction read row disappeared inside snapshot.");
                    ValidateSnapshot(row);
                    if (row.State != AuctionOrderState.Active || row.PendingOperationId != null || row.ExpiresAtUnix <= observedAtUnix
                        || (scope == AuctionOrderReadScope.SellerActive && (row.Seller.CharacterId != characterId || row.Seller.AccountId != accountId))
                        || (scope == AuctionOrderReadScope.CurrentHighestBidderActive
                            && (row.HighestBidder?.CharacterId != characterId || row.HighestBidder.AccountId != accountId || row.FundedBidGold <= 0)))
                        throw new InvalidOperationException("Auction read scope differs from validated order.");
                    rows.Add(row);
                }
                return new AuctionOrderReadSnapshot(true, "", observedAtUnix, rows);
            });
        }
        internal int CountActiveAndReservedAccountListings(int accountId)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            return database.Read(c => {
                using var cmd = Command(c, null, "SELECT count(*) FROM auction_order WHERE quota_key=@key AND (state IN (0,1) OR pending_operation_id IS NOT NULL);");
                Add(cmd, "@key", "account:" + accountId); return Convert.ToInt32(cmd.ExecuteScalar());
            });
        }
        internal IReadOnlyList<AuctionOrderSnapshot> FindDue(long nowUnix, long afterOrderId, int limit)
        {
            if (nowUnix <= 0 || afterOrderId < 0 || limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            return database.Read(c => {
                using var cmd = Command(c, null, @"SELECT order_id FROM auction_order WHERE state=1
AND pending_operation_id IS NULL AND expires_at_unix<=@now AND order_id>@after ORDER BY order_id LIMIT @limit;");
                Add(cmd, "@now", nowUnix); Add(cmd, "@after", afterOrderId); Add(cmd, "@limit", limit);
                var ids = new List<long>(); using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
                return (IReadOnlyList<AuctionOrderSnapshot>)ids.Select(id => Find(c, null, id)).ToArray();
            });
        }
        internal bool HasHeldAssetsForCharacter(int characterId)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            return database.Read(c => {
                using var cmd = Command(c, null, @"SELECT EXISTS(
SELECT 1 FROM auction_order WHERE (seller_character_id=@cid OR bidder_character_id=@cid)
 AND (state IN (0,1) OR pending_operation_id IS NOT NULL)
UNION ALL SELECT 1 FROM auction_operation WHERE character_id=@cid AND state IN (0,3));");
                Add(cmd, "@cid", characterId); return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
            });
        }
        internal bool HasUnresolvedOperationForCharacter(int characterId)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            return database.Read(c => {
                using var cmd = Command(c, null, "SELECT EXISTS(SELECT 1 FROM auction_operation WHERE character_id=@cid AND state IN (0,3));");
                Add(cmd, "@cid", characterId); return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
            });
        }
        internal IReadOnlyList<long> DeliveryMessageIds(int characterId)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            return database.Read(c => {
                using var cmd = Command(c, null, "SELECT public_message_id FROM auction_order_delivery WHERE recipient_character_id=@cid ORDER BY public_message_id;");
                Add(cmd, "@cid", characterId); var ids = new List<long>();
                using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetInt64(0)); return (IReadOnlyList<long>)ids.ToArray();
            });
        }
        internal AuctionJournalEntry FindOperation(Guid id) => database.Read(c => ReadJournal(c, null, id));
        internal IReadOnlyList<AuctionJournalEntry> Pending(int limit)
        {
            if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            return database.Read(c => {
                using var cmd = Command(c, null, "SELECT operation_id FROM auction_operation WHERE state=0 ORDER BY sequence_id LIMIT @limit");
                cmd.Parameters.AddWithValue("@limit", limit);
                var ids = new List<Guid>();
                using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Guid.ParseExact(r.GetString(0), "N"));
                return (IReadOnlyList<AuctionJournalEntry>)ids.Select(id => ReadJournal(c, null, id)).ToArray();
            });
        }

        internal AuctionOrderPreparation Prepare(AuctionOperationIntent intent, AuctionOrderTransition transition)
        {
            ValidateFrozen(intent, transition);
            return database.Write((c, t) => {
                var prior = AuctionOperationJournal.FindInTransaction(c, t, intent.OperationId);
                if (prior != null) { prior.Intent.RequireSame(intent); return new AuctionOrderPreparation(false, prior); }
                var current = Find(c, t, transition.After.OrderId);
                if (transition.Before == null)
                {
                    if (current != null || intent.Action != AuctionOperationAction.List || intent.ExpectedVersion != 0)
                        throw new InvalidOperationException("Auction listing identity already exists.");
                    using var quota = Command(c, t, @"SELECT count(*) FROM auction_order
WHERE quota_key=@key AND (state IN (0,1) OR pending_operation_id IS NOT NULL);");
                    quota.Parameters.AddWithValue("@key", transition.Quote.QuotaKey);
                    if (intent.Action == AuctionOperationAction.List && Convert.ToInt64(quota.ExecuteScalar()) >= transition.Quote.MaxActiveListings)
                        throw new InvalidOperationException("Auction listing quota exhausted.");
                }
                else if (current == null || current != transition.Before || current.PendingOperationId != null
                    || current.State != AuctionOrderState.Active || current.Version != intent.ExpectedVersion)
                    throw new InvalidOperationException("Auction order CAS conflict.");
                var entry = AuctionOperationJournal.PrepareInTransaction(c, t, intent);
                var reserved = transition.Before == null
                    ? transition.After with { Version = 0, State = AuctionOrderState.Funding,
                        FundedBidGold = 0, FundedDepositGold = 0, PendingOperationId = intent.OperationId }
                    : current with { PendingOperationId = intent.OperationId };
                Save(c, t, reserved, insert: transition.Before == null);
                return new AuctionOrderPreparation(true, entry);
            });
        }

        internal AuctionOrderSnapshot Complete(AuctionOperationIntent intent, AuctionPublicReceipt receipt)
        {
            if (receipt == null || receipt.FormatVersion != 1 || receipt.OperationId != intent.OperationId
                || receipt.IntentHash != intent.IntentHash || receipt.CharacterId != intent.CharacterId
                || receipt.AccountId != intent.AccountId) throw new AuctionReceiptConflictException("Order receipt conflict.");
            var transition = ReadTransition(intent);
            ValidateFrozen(intent, transition);
            var plan = ReadPlan(intent);
            long[] messageIds;
            try
            {
                using var result = JsonDocument.Parse(receipt.ResultJson);
                var body = result.RootElement;
                messageIds = body.GetProperty("mailMessageIds").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                if (body.GetProperty("formatVersion").GetInt32() != 1
                    || body.GetProperty("goldDebited").GetInt32() != plan.GoldDebit
                    || body.GetProperty("itemDebitCount").GetInt32() != plan.ItemDebits.Count
                    || messageIds.Length != transition.Legs.Count || messageIds.Any(x => x <= 0)
                    || messageIds.Distinct().Count() != messageIds.Length)
                    throw new AuctionReceiptConflictException("Auction receipt asset result conflict.");
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException
                || ex is FormatException || ex is OverflowException)
            { throw new AuctionReceiptConflictException("Malformed or inconsistent auction asset receipt result."); }
            return database.Write((c, t) => {
                var entry = AuctionOperationJournal.FindInTransaction(c, t, intent.OperationId)
                    ?? throw new InvalidOperationException("Order operation was not prepared.");
                entry.Intent.RequireSame(intent);
                if (entry.State == AuctionOperationState.Committed)
                {
                    if (entry.ResultJson != receipt.ResultJson) throw new AuctionReceiptConflictException("Order terminal receipt conflict.");
                    // The current order may have advanced since this operation. Return its frozen result.
                    return transition.After;
                }
                if (entry.State != AuctionOperationState.Prepared) throw new InvalidOperationException("Order is held or aborted.");
                RequireReservation(c, t, intent, transition);
                Save(c, t, transition.After, false);
                for (int i = 0; i < transition.Legs.Count; i++)
                {
                    var leg = transition.Legs[i];
                    using var cmd = Command(c, t, @"INSERT INTO auction_order_delivery
(operation_id,leg_key,order_id,recipient_character_id,public_message_id,leg_json)
VALUES(@op,@leg,@order,@cid,@mail,@json);");
                    Add(cmd, "@op", intent.OperationId.ToString("N")); Add(cmd, "@leg", leg.LegKey);
                    Add(cmd, "@order", transition.After.OrderId); Add(cmd, "@cid", leg.Recipient.CharacterId);
                    Add(cmd, "@mail", messageIds[i]); Add(cmd, "@json", JsonSerializer.Serialize(leg)); cmd.ExecuteNonQuery();
                }
                AuctionOperationJournal.ResolveInTransaction(c, t, intent, AuctionOperationState.Committed, receipt.ResultJson, "");
                return transition.After;
            });
        }

        internal void AbortVerifiedUncommitted(AuctionOperationIntent intent, string reason)
            => ResolveWithoutPublicCommit(intent, reason, false);
        internal void Quarantine(AuctionOperationIntent intent, string reason)
            => ResolveWithoutPublicCommit(intent, reason, true);

        private void ResolveWithoutPublicCommit(AuctionOperationIntent intent, string reason, bool quarantine)
        {
            var transition = ReadTransition(intent);
            database.Write((c, t) => {
                var entry = AuctionOperationJournal.FindInTransaction(c, t, intent.OperationId)
                    ?? throw new InvalidOperationException("Order operation was not prepared.");
                entry.Intent.RequireSame(intent);
                var target = quarantine ? AuctionOperationState.Quarantined : AuctionOperationState.Aborted;
                if (entry.State == AuctionOperationState.Prepared)
                {
                    RequireReservation(c, t, intent, transition);
                    if (!quarantine)
                    {
                        if (transition.Before == null)
                        {
                            using var cmd = Command(c, t, "DELETE FROM auction_order WHERE order_id=@id AND pending_operation_id=@op;");
                            Add(cmd, "@id", transition.After.OrderId); Add(cmd, "@op", intent.OperationId.ToString("N"));
                            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Order reservation changed.");
                        }
                        else Save(c, t, transition.Before, false);
                    }
                }
                AuctionOperationJournal.ResolveInTransaction(c, t, intent, target, "{}", reason);
                return true;
            });
        }

        private void ValidateFrozen(AuctionOperationIntent intent, AuctionOrderTransition x)
        {
            if (x == null || ReadTransition(intent).Serialize() != x.Serialize()) throw new ArgumentException("Transition differs from frozen intent.");
            var a = x.After; var b = x.Before; var q = x.Quote; var binding = q.Binding;
            ValidateSnapshot(a);
            if (b != null) ValidateSnapshot(b);
            if (a.PendingOperationId != null || a.Version != checked((b?.Version ?? 0) + 1)
                || a.State == AuctionOrderState.Funding || binding == null
                || binding.Action != intent.Action || binding.OrderId != a.OrderId
                || binding.ExpectedVersion != intent.ExpectedVersion || binding.ActorCharacterId != intent.CharacterId
                || binding.ActorAccountId != intent.AccountId || string.IsNullOrWhiteSpace(binding.RequestFingerprint)
                || q.Availability == AuctionQuoteAvailability.Unavailable || q.ValidUntilUnix < intent.CreatedAtUnix
                || q.MaxActiveListings <= 0 || q.QuotaKey != a.QuotaKey)
                throw new ArgumentException("Invalid auction transition binding.");
            if (intent.ResourceKey != "order:" + a.OrderId || (b == null) != (intent.Action == AuctionOperationAction.List)
                || (b == null && (a.Seller.CharacterId != intent.CharacterId || a.Seller.AccountId != intent.AccountId))
                || (b != null && intent.ExpectedVersion != b.Version))
                throw new ArgumentException("Auction intent order identity differs.");
            if (!Enum.IsDefined(q.Availability)
                || (q.Availability == AuctionQuoteAvailability.FixtureOnly ? !allowFixturePolicies
                    : q.Availability != AuctionQuoteAvailability.ApprovedProductPolicy || q.PolicyVersion != AuctionEconomyPolicy.PolicyVersion)
                || q.PolicyVersion != a.PolicyVersion || q.FeeRatePercent != a.FeeRatePercent || q.UnitPriceGold != a.UnitPriceGold
                || q.MaxActiveListings is not (5 or 30))
                throw new ArgumentException("Unsupported auction economic policy.");
            if(intent.Action==AuctionOperationAction.List&&a.FeeRatePercent is not(3 or 5))
                throw new ArgumentException("Product listing fee must be 3% or 5%.");
            if (b != null && (b.OrderId != a.OrderId || b.Seller != a.Seller || b.SourceEnvelopeJson != a.SourceEnvelopeJson
                || b.InitialQuantity != a.InitialQuantity || b.CreatedAtUnix != a.CreatedAtUnix || b.ExpiresAtUnix != a.ExpiresAtUnix
                || b.QuotaKey != a.QuotaKey || b.PolicyVersion != a.PolicyVersion || b.FeeRatePercent != a.FeeRatePercent
                || b.UnitPriceGold != a.UnitPriceGold || b.PendingOperationId != null))
                throw new ArgumentException("Immutable auction terms changed.");
            var money = new[] { q.GrossGold, q.RegistrationFeeGold, q.DepositDebitGold, q.SettlementFeeGold, q.DepositRefundGold, q.DepositForfeitGold };
            if (money.Any(v => v < 0 || v > int.MaxValue)) throw new ArgumentException("Invalid auction quote money.");
            var plan = ReadPlan(intent); var mails = plan.CopySystemMails();
            var lineage = codec.DeserializeStrict(b?.EscrowEnvelopeJson ?? a.SourceEnvelopeJson);
            // Validate the narrow transfer derivative independently of the
            // planner: stack quantity or detail-bearing receiver UID rebasing.
            // Every other native byte, detail and private field must survive.
            if (a.State == AuctionOrderState.Active)
                RequireQuantityDerivative(lineage, a.EscrowEnvelopeJson, a.RemainingQuantity);
            else if (b != null && a.EscrowEnvelopeJson != b.EscrowEnvelopeJson)
                throw new ArgumentException("Terminal escrow evidence changed.");
            if (mails.Count != x.Legs.Count || x.Legs.Select(l => l.LegKey).Distinct().Count() != x.Legs.Count)
                throw new ArgumentException("Delivery legs do not match public plan.");
            if (x.Legs.GroupBy(l => l.Kind).Any(g => g.Count() > 1))
                throw new ArgumentException("Duplicate auction delivery kind.");
            long mailGold = 0, itemQuantity = 0;
            for (int i = 0; i < x.Legs.Count; i++)
            {
                var leg = x.Legs[i]; var mail = mails[i]; RequireParty(leg.Recipient);
                ValidateDeliveryOwner(intent, x, leg);
                if (!Enum.IsDefined(leg.Kind) || string.IsNullOrWhiteSpace(leg.LegKey) || leg.Gold < 0
                    || mail.ReceiverCharacterId != leg.Recipient.CharacterId || mail.ReceiverAccountId != leg.Recipient.AccountId
                    || mail.Gold != leg.Gold || mail.IdempotencyKey != "auction:" + intent.OperationId.ToString("N") + ":" + leg.LegKey)
                    throw new ArgumentException("Public mail differs from delivery leg.");
                mailGold = checked(mailGold + leg.Gold);
                if (leg.ItemEnvelopeJson == null)
                { if (leg.Quantity != 0 || mail.Attachments.Count != 0) throw new ArgumentException("Unexpected attachment."); }
                else
                {
                    var envelope = codec.DeserializeStrict(leg.ItemEnvelopeJson);
                    var core = codec.DecodeStrict(envelope);
                    RequireQuantityDerivative(lineage, leg.ItemEnvelopeJson, leg.Quantity);
                    if (leg.Quantity <= 0 || mail.Attachments.Count != 1 || mail.Attachments[0].ItemId != core.ItemId
                        || mail.Attachments[0].ItemCount != leg.Quantity
                        || !mail.Attachments[0].ItemCoreData.SequenceEqual(envelope.NativeCore99)
                        || mail.Attachments[0].DetailJson != envelope.DetailJson
                        || mail.Attachments[0].ExtraJson != envelope.ExtensionsJson
                        || (InventoryStackRuleService.IsStackable(core) ? core.Count != leg.Quantity : leg.Quantity != 1))
                        throw new ArgumentException("Item delivery mismatch.");
                    itemQuantity = checked(itemQuantity + leg.Quantity);
                }
            }
            if (checked((b?.FundedBidGold ?? 0) + (b?.FundedDepositGold ?? 0) + plan.GoldDebit)
                != checked(a.FundedBidGold + a.FundedDepositGold + mailGold + q.RegistrationFeeGold + q.SettlementFeeGold + q.DepositForfeitGold))
                throw new ArgumentException("Auction gold is not conserved.");
            if (checked((b?.RemainingQuantity ?? 0) + plan.ItemDebits.Sum(d => (long)d.Count)) != checked(a.RemainingQuantity + itemQuantity)
                || (intent.Action != AuctionOperationAction.List && plan.ItemDebits.Count != 0))
                throw new ArgumentException("Auction items are not conserved.");
            if (b == null && (plan.ItemDebits.Count != 1 || plan.ItemDebits[0].Count != a.InitialQuantity
                || codec.Serialize(plan.ItemDebits[0].Source) != a.SourceEnvelopeJson))
                throw new ArgumentException("Auction listing debit differs from its owned source.");
        }

        private static void ValidateDeliveryOwner(AuctionOperationIntent intent, AuctionOrderTransition x, AuctionDeliveryLeg leg)
        {
            bool sale = intent.Action is AuctionOperationAction.Buyout or AuctionOperationAction.BuyPiece
                || intent.Action == AuctionOperationAction.Expire && x.Before?.HighestBidder != null;
            AuctionParty expected;
            switch (leg.Kind)
            {
                case AuctionDeliveryKind.ItemDelivery:
                    if (!sale || leg.Gold != 0 || leg.ItemEnvelopeJson == null) throw new ArgumentException("Invalid auction sale item leg.");
                    expected = intent.Action == AuctionOperationAction.Expire ? x.Before.HighestBidder
                        : new AuctionParty(intent.CharacterId, intent.AccountId, leg.Recipient.FrozenName);
                    break;
                case AuctionDeliveryKind.ItemReturn:
                    if (sale || intent.Action is not (AuctionOperationAction.Cancel or AuctionOperationAction.Expire)
                        || leg.Gold != 0 || leg.ItemEnvelopeJson == null) throw new ArgumentException("Invalid auction item return leg.");
                    expected = x.After.Seller;
                    break;
                case AuctionDeliveryKind.SellerProceeds:
                    if (!sale || leg.Gold <= 0 || leg.ItemEnvelopeJson != null) throw new ArgumentException("Invalid seller proceeds leg.");
                    expected = x.After.Seller;
                    break;
                case AuctionDeliveryKind.DepositRefund:
                    if ((!sale && intent.Action != AuctionOperationAction.Expire) || leg.Gold <= 0 || leg.ItemEnvelopeJson != null)
                        throw new ArgumentException("Invalid deposit refund leg.");
                    expected = x.After.Seller;
                    break;
                case AuctionDeliveryKind.BidRefund:
                    if (intent.Action is not (AuctionOperationAction.Bid or AuctionOperationAction.Buyout)
                        || x.Before?.HighestBidder == null || leg.Gold != x.Before.FundedBidGold || leg.ItemEnvelopeJson != null
                        || x.Before.HighestBidder.CharacterId == intent.CharacterId)
                        throw new ArgumentException("Invalid old bid refund leg.");
                    expected = x.Before.HighestBidder;
                    break;
                default: throw new ArgumentException("Unknown auction delivery kind.");
            }
            if (leg.Recipient.CharacterId != expected.CharacterId || leg.Recipient.AccountId != expected.AccountId)
                throw new ArgumentException("Auction liability recipient differs from its owner.");
        }

        private void RequireQuantityDerivative(AuctionItemEnvelope source, string finalJson, int quantity)
        {
            if (quantity <= 0) throw new ArgumentException("Invalid escrow derivative quantity.");
            var core = codec.DecodeStrict(source);
            if (InventoryStackRuleService.IsStackable(core))
            {
                if (quantity > core.Count) throw new ArgumentException("Escrow derivative exceeds owned quantity.");
                core.Count = quantity;
            }
            else if (quantity != 1) throw new ArgumentException("Nonstackable escrow quantity differs.");
            // MailboxRepository allocates a fresh receiver UID in the actual
            // claim transaction. The original UID stays in SourceEnvelopeJson.
            if (core.ItemKind == ItemCore.KindAvatar) core.AvatarUid = 0;
            else if (core.ItemKind == ItemCore.KindCreature) core.CreatureUid = 0;
            if (codec.Serialize(codec.CreateTransferSnapshot(source, core)) != finalJson)
                throw new ArgumentException("Auction item lineage differs from owned escrow.");
        }

        private void ValidateSnapshot(AuctionOrderSnapshot x)
        {
            RequireParty(x.Seller);
            if (x.OrderId <= 0 || x.Version < 0 || !Enum.IsDefined(x.State) || x.InitialQuantity <= 0
                || x.RemainingQuantity < 0 || x.RemainingQuantity > x.InitialQuantity || x.CreatedAtUnix <= 0 || x.ExpiresAtUnix <= x.CreatedAtUnix
                || x.FundedBidGold < 0 || x.FundedDepositGold < 0 || x.CumulativeGrossGold < 0 || x.CumulativeFeeGold < 0
                || x.QuotaKey != "account:" + x.Seller.AccountId || string.IsNullOrWhiteSpace(x.PolicyVersion)
                || x.FeeRatePercent < 0 || x.FeeRatePercent > 100 || (x.HighestBidder == null) != (x.FundedBidGold == 0)
                || (x.State == AuctionOrderState.Active && x.RemainingQuantity == 0)
                || (x.State is AuctionOrderState.Sold or AuctionOrderState.Cancelled or AuctionOrderState.ExpiredUnsold
                    && (x.RemainingQuantity != 0 || x.FundedBidGold != 0 || x.FundedDepositGold != 0)))
                throw new ArgumentException("Invalid auction order snapshot.");
            if (x.HighestBidder != null) { RequireParty(x.HighestBidder); if (x.HighestBidder.AccountId == x.Seller.AccountId) throw new ArgumentException("Self bid."); }
            var source = codec.DecodeStrict(codec.DeserializeStrict(x.SourceEnvelopeJson));
            var escrow = codec.DecodeStrict(codec.DeserializeStrict(x.EscrowEnvelopeJson));
            if (source.ItemId != escrow.ItemId || (x.State == AuctionOrderState.Active
                && (InventoryStackRuleService.IsStackable(escrow) ? escrow.Count != x.RemainingQuantity : x.RemainingQuantity != 1)))
                throw new ArgumentException("Escrow differs from its owned remainder.");
        }

        private static void RequireParty(AuctionParty p)
        { if (p == null || p.CharacterId <= 0 || p.AccountId <= 0 || string.IsNullOrWhiteSpace(p.FrozenName)) throw new ArgumentException("Invalid auction party."); }
        private AuctionPublicAssetPlan ReadPlan(AuctionOperationIntent intent)
            => AuctionPublicAssetPlan.DeserializeStrict(codec, FrozenString(intent, "publicAssetPlan"));
        private static AuctionOrderTransition ReadTransition(AuctionOperationIntent intent)
            => AuctionOrderTransition.DeserializeStrict(FrozenString(intent, "orderTransition"));
        private static string FrozenString(AuctionOperationIntent intent, string name)
        {
            using var doc = JsonDocument.Parse(intent.IntentJson);
            if (doc.RootElement.EnumerateObject().Count(p => p.Name == name) != 1) throw new ArgumentException("Missing or duplicate frozen descriptor.");
            return doc.RootElement.GetProperty(name).GetString() ?? throw new ArgumentException("Missing descriptor.");
        }
        private static void RequireReservation(SqliteConnection c, SqliteTransaction t, AuctionOperationIntent intent, AuctionOrderTransition transition)
        {
            var current = Find(c, t, transition.After.OrderId);
            var expected = transition.Before == null
                ? transition.After with { Version = 0, State = AuctionOrderState.Funding, FundedBidGold = 0, FundedDepositGold = 0, PendingOperationId = intent.OperationId }
                : transition.Before with { PendingOperationId = intent.OperationId };
            if (current != expected) throw new InvalidOperationException("Auction order reservation conflict.");
        }
        private static AuctionJournalEntry ReadJournal(SqliteConnection c, SqliteTransaction t, Guid id)
        {
            // Read uses the same repository validation without opening another connection.
            if (t != null) return AuctionOperationJournal.FindInTransaction(c, t, id);
            using var read = c.BeginTransaction(deferred: true);
            return AuctionOperationJournal.FindInTransaction(c, read, id);
        }
        private static AuctionOrderSnapshot Find(SqliteConnection c, SqliteTransaction t, long id)
        {
            using var cmd = Command(c, t, "SELECT version,state,seller_character_id,seller_account_id,bidder_character_id,quota_key,expires_at_unix,pending_operation_id,snapshot_json,snapshot_hash FROM auction_order WHERE order_id=@id;");
            Add(cmd, "@id", id); using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
            var json = r.GetString(8); var x = JsonSerializer.Deserialize<AuctionOrderSnapshot>(json) ?? throw new InvalidOperationException("Missing order snapshot.");
            if (Hash(json) != r.GetString(9) || JsonSerializer.Serialize(x) != json || x.OrderId != id || x.Version != r.GetInt64(0)
                || (int)x.State != r.GetInt32(1) || x.Seller.CharacterId != r.GetInt32(2) || x.Seller.AccountId != r.GetInt32(3)
                || x.HighestBidder?.CharacterId != (r.IsDBNull(4) ? (int?)null : r.GetInt32(4))
                || x.QuotaKey != r.GetString(5) || x.ExpiresAtUnix != r.GetInt64(6)
                || x.PendingOperationId?.ToString("N") != (r.IsDBNull(7) ? null : r.GetString(7)))
                throw new InvalidOperationException("Corrupt auction order projection.");
            return x;
        }
        private static void Save(SqliteConnection c, SqliteTransaction t, AuctionOrderSnapshot x, bool insert)
        {
            var json = JsonSerializer.Serialize(x);
            using var cmd = Command(c, t, insert
                ? "INSERT INTO auction_order VALUES(@id,@version,@state,@cid,@aid,@bidder,@quota,@expires,@pending,@json,@hash);"
                : @"UPDATE auction_order SET version=@version,state=@state,seller_character_id=@cid,seller_account_id=@aid,
bidder_character_id=@bidder,quota_key=@quota,expires_at_unix=@expires,pending_operation_id=@pending,snapshot_json=@json,snapshot_hash=@hash WHERE order_id=@id;");
            Add(cmd, "@id", x.OrderId); Add(cmd, "@version", x.Version); Add(cmd, "@state", (int)x.State);
            Add(cmd, "@cid", x.Seller.CharacterId); Add(cmd, "@aid", x.Seller.AccountId); Add(cmd, "@bidder", x.HighestBidder?.CharacterId);
            Add(cmd, "@quota", x.QuotaKey); Add(cmd, "@expires", x.ExpiresAtUnix); Add(cmd, "@pending", x.PendingOperationId?.ToString("N"));
            Add(cmd, "@json", json); Add(cmd, "@hash", Hash(json));
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Auction order write conflict.");
        }
        private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        private static void Add(SqliteCommand cmd, string name, object value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        private static SqliteCommand Command(SqliteConnection c, SqliteTransaction t, string sql)
        { var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; return cmd; }
    }
}
