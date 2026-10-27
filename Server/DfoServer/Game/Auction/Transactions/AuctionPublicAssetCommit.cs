using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Transactions
{
    internal enum AuctionAssetCommitState { Committed, Rejected, Pending, Conflict }
    internal sealed record AuctionAssetCommitResult(AuctionAssetCommitState State, AuctionPublicReceipt Receipt, string Reason);
    internal enum AuctionAssetFaultPoint { BeforePublicCommit, AfterPublicCommit }

    // Unregistered primitive. The same gate MUST also protect the private
    // Prepare/finalize/order owner and all recovery. This class never resolves
    // a private reservation, retries a debit or owns a second online wallet.
    internal sealed class AuctionPublicAssetCommit
    {
        private readonly IGameDatabase database;
        private readonly AuctionItemCodec codec;
        private readonly MailboxRepository mailbox;
        private readonly object marketGate;
        private readonly Action<AuctionAssetFaultPoint> fault;
        // A same-process safety fence only. Durable restart decisions belong to
        // the main journal recovery; Prepared intents are not replayed here.
        private readonly Dictionary<Guid, string> attempted = new();

        internal AuctionPublicAssetCommit(IGameDatabase database, AuctionItemCodec codec,
            object marketGate, Action<AuctionAssetFaultPoint> fault = null)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.marketGate = marketGate ?? throw new ArgumentNullException(nameof(marketGate));
            this.fault = fault;
            mailbox = new MailboxRepository(database);
        }

        internal AuctionAssetCommitResult ReadOutcome(AuctionOperationIntent intent)
        {
            lock (marketGate)
            {
                var outcome = Lookup(intent, out _);
                return outcome ?? new(AuctionAssetCommitState.Pending, null, "NoCommittedReceiptRequiresPrivateRecovery");
            }
        }

        internal AuctionAssetCommitResult Execute(AuctionOperationIntent intent, AuctionPublicAssetPlan plan, InventoryLease lease,
            Func<SqliteConnection, SqliteTransaction, bool> precondition = null)
        {
            if (intent == null || plan == null) throw new ArgumentNullException();
            RequireBoundPlan(intent, plan);
            lock (marketGate)
            {
                var found = Lookup(intent, out var knownAbsent);
                if (found != null)
                {
                    if (found.State == AuctionAssetCommitState.Pending)
                        attempted.TryAdd(intent.OperationId, intent.IntentHash);
                    return found;
                }
                if (!knownAbsent) return new(AuctionAssetCommitState.Pending, null, "PublicReceiptUnavailable");
                if (attempted.TryGetValue(intent.OperationId, out var hash))
                    return new(hash == intent.IntentHash ? AuctionAssetCommitState.Pending : AuctionAssetCommitState.Conflict,
                        null, "OperationAlreadyAttemptedRequiresPrivateRecovery");
                attempted.Add(intent.OperationId, intent.IntentHash);
                if (!Current(lease, intent)) return new(AuctionAssetCommitState.Rejected, null, "StaleInventoryLease");
                lock (lease.SyncRoot)
                {
                    if (!Current(lease, intent)) return new(AuctionAssetCommitState.Rejected, null, "StaleInventoryLease");
                    if (!InventoryPersistenceService.SaveDirty(lease))
                        return AfterAttempt(intent, false, "BaselineSaveFailed");
                    bool committed = false;
                    bool inventoryTouched = false;
                    var baselineInventory = lease.Inventory;
                    try
                    {
                        committed = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "auction-public-assets", (c, t) =>
                        {
                            if (AuctionPublicReceiptRepository.Find(c, t, intent) != null) return true;
                            if (precondition != null && !precondition(c, t)) return false;
                            if (!Current(lease, intent) || !Active(c, t, intent.CharacterId, intent.AccountId)) return false;
                            var messages = plan.CopySystemMails();
                            foreach (var message in messages)
                                if (!Active(c, t, message.SenderCharacterId, message.SenderAccountId)
                                    || !Active(c, t, message.ReceiverCharacterId, message.ReceiverAccountId)) return false;
                            var inventory = lease.Inventory;
                            int gold = inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                            if (gold < plan.GoldDebit) return false;
                            foreach (var debit in plan.ItemDebits)
                            {
                                if (!codec.MatchesSource(inventory, debit.List, debit.Slot, debit.Source)) return false;
                                var core = inventory.GetItem(debit.List, debit.Slot);
                                if (core.EquipmentLockId != 0 || core.SortLockFlag != 0) return false;
                            }
                            inventoryTouched = plan.ItemDebits.Count > 0 || plan.GoldDebit > 0;
                            foreach (var debit in plan.ItemDebits)
                            {
                                var source = codec.DecodeStrict(debit.Source);
                                if (!InventoryDeleteService.TryConsumeFromSlot(inventory, debit.List, debit.Slot,
                                    source.ItemId, debit.Count, out var deleted) || !deleted.Success || deleted.DeletedCount != debit.Count) return false;
                            }
                            if (plan.GoldDebit > 0 && !inventory.SetMainVirtualCount(
                                InventoryService.MainVirtualCurrencySlotStart, checked(gold - plan.GoldDebit))) return false;
                            var mailIds = new List<long>();
                            foreach (var message in messages)
                            {
                                var sent = mailbox.SendSystemMails(c, t, new[] { message });
                                if (!sent.Success || sent.MessageId <= 0) return false;
                                mailIds.Add(sent.MessageId);
                            }
                            var result = JsonSerializer.Serialize(new { formatVersion = 1, goldDebited = plan.GoldDebit,
                                itemDebitCount = plan.ItemDebits.Count, mailMessageIds = mailIds });
                            AuctionPublicReceiptRepository.Insert(c, t, intent, lease.SessionId, result);
                            fault?.Invoke(AuctionAssetFaultPoint.BeforePublicCommit);
                            return true;
                        }, immediate: true);
                        if (committed) fault?.Invoke(AuctionAssetFaultPoint.AfterPublicCommit);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is SqliteException)
                    {
                        // No second debit attempt. The permanent receipt decides.
                    }
                    if (!committed && inventoryTouched && ReferenceEquals(baselineInventory, lease.Inventory))
                    {
                        // The shared coordinator normally replaces Inventory on
                        // rollback. If its reload failed, absence of a receipt
                        // alone does not make the mutated online model safe.
                        var outcome = Lookup(intent, out _);
                        return outcome ?? new(AuctionAssetCommitState.Pending, null, "InventoryRollbackUnconfirmed");
                    }
                    return AfterAttempt(intent, committed, "PublicTransactionNotCommitted");
                }
            }
        }

        // Only the order owner may invoke this AFTER durable private Prepare
        // proves the delivery is funded by existing escrow. No offline wallet
        // or fabricated inventory lease is constructed.
        internal AuctionAssetCommitResult ExecuteFundedExpiry(AuctionOperationIntent intent, AuctionPublicAssetPlan plan)
        {
            if (intent == null || plan == null) throw new ArgumentNullException();
            RequireBoundPlan(intent, plan);
            if (intent.Action != AuctionOperationAction.Expire
                || plan.GoldDebit != 0 || plan.ItemDebits.Count != 0)
                throw new ArgumentException("Offline auction delivery must be a funded expiry without public debits.");
            lock (marketGate)
            {
                var found = Lookup(intent, out var absent);
                if (found != null)
                {
                    if (found.State == AuctionAssetCommitState.Pending) attempted.TryAdd(intent.OperationId, intent.IntentHash);
                    return found;
                }
                if (!absent) return new(AuctionAssetCommitState.Pending, null, "PublicReceiptUnavailable");
                if (attempted.TryGetValue(intent.OperationId, out var hash))
                    return new(hash == intent.IntentHash ? AuctionAssetCommitState.Pending : AuctionAssetCommitState.Conflict,
                        null, "OperationAlreadyAttemptedRequiresPrivateRecovery");
                attempted.Add(intent.OperationId, intent.IntentHash);
                bool committed = false;
                try
                {
                    committed = database.Write((c, t) => {
                        var prior = AuctionPublicReceiptRepository.Find(c, t, intent);
                        if (prior != null) return true;
                        var messages = plan.CopySystemMails();
                        if (!Active(c, t, intent.CharacterId, intent.AccountId)) return false;
                        foreach (var mail in messages)
                            if (!Active(c, t, mail.SenderCharacterId, mail.SenderAccountId)
                                || !Active(c, t, mail.ReceiverCharacterId, mail.ReceiverAccountId)) return false;
                        var ids = new List<long>();
                        foreach (var message in messages)
                        {
                            var sent = mailbox.SendSystemMails(c, t, new[] { message });
                            if (!sent.Success || sent.MessageId <= 0) throw new InvalidOperationException("Funded auction mail delivery failed.");
                            ids.Add(sent.MessageId);
                        }
                        var result = JsonSerializer.Serialize(new { formatVersion = 1, goldDebited = 0,
                            itemDebitCount = 0, mailMessageIds = ids });
                        AuctionPublicReceiptRepository.Insert(c, t, intent, Guid.Empty, result);
                        fault?.Invoke(AuctionAssetFaultPoint.BeforePublicCommit);
                        return true;
                    }, immediate: true);
                    if (committed) fault?.Invoke(AuctionAssetFaultPoint.AfterPublicCommit);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is SqliteException) { }
                return AfterAttempt(intent, committed, "FundedDeliveryNotCommitted");
            }
        }

        internal void ReleaseTerminalAttempt(AuctionJournalEntry entry)
        {
            if (entry == null || entry.State is not (AuctionOperationState.Committed or AuctionOperationState.Aborted))
                throw new ArgumentException("Only a verified terminal operation releases its transient attempt fence.");
            lock (marketGate)
            {
                if (attempted.TryGetValue(entry.Intent.OperationId, out var hash) && hash != entry.Intent.IntentHash)
                    throw new InvalidOperationException("Auction attempt identity conflict.");
                attempted.Remove(entry.Intent.OperationId);
            }
        }

        private AuctionAssetCommitResult AfterAttempt(AuctionOperationIntent intent, bool reportedCommit, string absentReason)
        {
            var outcome = Lookup(intent, out var absent);
            if (outcome?.State == AuctionAssetCommitState.Committed) attempted.Remove(intent.OperationId);
            return outcome ?? new(reportedCommit || !absent ? AuctionAssetCommitState.Pending : AuctionAssetCommitState.Rejected,
                null, reportedCommit ? "CommittedReceiptMissing" : absentReason);
        }

        private AuctionAssetCommitResult Lookup(AuctionOperationIntent intent, out bool absent)
        {
            absent = false;
            try
            {
                var receipt = database.Read(c => AuctionPublicReceiptRepository.Find(c, null, intent));
                if (receipt != null) return new(AuctionAssetCommitState.Committed, receipt, "");
                absent = true; return null;
            }
            catch (Exception ex) when (ex is AuctionReceiptConflictException || ex is JsonException)
            { return new(AuctionAssetCommitState.Conflict, null, "PublicReceiptConflict"); }
            catch (Exception ex) when (ex is SqliteException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            { return new(AuctionAssetCommitState.Pending, null, "PublicReceiptUnavailable"); }
        }

        private bool Current(InventoryLease lease, AuctionOperationIntent intent)
            => lease?.Inventory?.Database != null && lease.CharacterId == intent.CharacterId && lease.AccountId == intent.AccountId
                && lease.Inventory.Database.ConnectionString == database.ConnectionString
                && InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);

        private static bool Active(SqliteConnection c, SqliteTransaction t, int cid, int aid)
        {
            using var command = c.CreateCommand(); command.Transaction = t;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM characters WHERE character_id=@cid AND account_id=@aid AND delete_flag=0);";
            command.Parameters.AddWithValue("@cid", cid); command.Parameters.AddWithValue("@aid", aid);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        private static void RequireBoundPlan(AuctionOperationIntent intent, AuctionPublicAssetPlan plan)
        {
            using var document = JsonDocument.Parse(intent.IntentJson);
            int count = 0;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name == "publicAssetPlan")
                {
                    count++;
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != plan.Serialize())
                        throw new ArgumentException("Auction asset plan is not bound to the frozen intent.");
                }
            if (count != 1) throw new ArgumentException("Frozen intent requires one publicAssetPlan string.");
            foreach (var mail in plan.CopySystemMails())
                if (!mail.IdempotencyKey.StartsWith("auction:" + intent.OperationId.ToString("N") + ":", StringComparison.Ordinal))
                    throw new ArgumentException("Auction mail leg key must be bound to its operation UUID.");
        }
    }
}
