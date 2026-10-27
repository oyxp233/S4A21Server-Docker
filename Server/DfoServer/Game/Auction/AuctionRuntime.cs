using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Rules;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Auction;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Auction;

namespace DfoServer.Game.Auction;

// One instance per ServerRuntimeBuilder, shared by all channel listeners.
// Disabling new entry must not disable recovery, funded expiry or deletion guards.
internal sealed class AuctionRuntime : IDisposable
{
    private readonly IGameDatabase publicDatabase;
    private readonly Lazy<AuctionTemplateCatalog> catalog;
    private readonly Lazy<AuctionQueryEngine> query;
    private readonly AuctionOrderRecovery recovery;
    private readonly AuctionOrderExpiryProcessor expiry;
    private readonly AuctionPublicAssetCommit rail;
    private readonly Func<long> now;
    private ClockService.ClockTimerHandle timer;
    private bool disposed;
    private readonly bool requestedEntryEnabled;
    private readonly SqliteAuctionTradeHistory history;

    internal AuctionRuntime(IGameDatabase publicDatabase, string privatePath,
        Func<AuctionTemplateCatalog> loadCatalog, Func<long> nowUnix, bool entryEnabled,
        Action<AuctionAssetFaultPoint> assetFault = null, string historyDatabasePath = null)
    {
        this.publicDatabase = publicDatabase ?? throw new ArgumentNullException(nameof(publicDatabase));
        now = nowUnix ?? throw new ArgumentNullException(nameof(nowUnix));
        catalog = new(loadCatalog ?? throw new ArgumentNullException(nameof(loadCatalog)));
        requestedEntryEnabled = entryEnabled && !RecoveryOnlyBuild;
        Gate = new object();
        Codec = new(new NativeAuctionInstanceExtension());
        Store = new(privatePath, Codec);
        string historyPath = Path.GetFullPath(historyDatabasePath
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(privatePath)), "auction-history.db"));
        if (string.Equals(historyPath, Path.GetFullPath(privatePath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(historyPath, Path.GetFullPath(publicDatabase.DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("History must use an independent database file.");
        history = new(historyPath, Codec);
        query = new(() => new AuctionQueryEngine(catalog.Value, Codec,
            new AuctionNativeCategoryResolver(catalog.Value).Resolve));
        rail = new AuctionPublicAssetCommit(publicDatabase, Codec, Gate, assetFault);
        var transformer = new AuctionTransferableItemAdapter(Codec);
        var planner = new AuctionMarketPlanner(Codec, transformer.Transform);
        var quote = new AuctionApprovedQuoteProvider(ReadEntitlements);
        Application = new(Store, Codec, planner, null, quote.Quote, rail,
            CreateDeliveryMail, Gate, now,
            deferredListingEligibility: () => new AuctionListingEligibility(catalog.Value, Codec,
                AuctionNativeListingRuleResolver.Resolve),
            canRepresentListing: item => AuctionNativeDisplayProjection.TryProject(item, Codec, out _, out _),
            entryPolicy: request => request.Action==AuctionOperationAction.Expire
                || (!RecoveryOnlyBuild&&EntryEnabled));
        recovery = new(Store, publicDatabase, rail, Gate);
        expiry = new(Store, Application, Gate);
        DeletionGuard = new(Store, publicDatabase, Gate);
    }

    internal object Gate { get; }
    internal static bool RecoveryOnlyBuild
    {
        get
        {
#if AUCTION_RECOVERY_ONLY
            return true;
#else
            return false;
#endif
        }
    }
    internal bool EntryEnabled { get; private set; }
    internal AuctionItemCodec Codec { get; }
    internal AuctionOrderStore Store { get; }
    internal IAuctionTradeHistoryReader TradeHistory => history;
    internal AuctionMarketApplication Application { get; }
    internal AuctionPublicAssetCommit AssetRail => rail;
    internal AuctionCharacterLiabilityGuard DeletionGuard { get; }
    internal AuctionQueryEngine Query => query.Value;
    internal AuctionTemplateCatalog Catalog => catalog.Value;
    internal long NowUnix => now();
    internal event Action<IReadOnlyList<int>> DeliveriesCommitted;

    internal void PublishCommitted(AuctionMarketResult result)
    {
        if (result?.Status != AuctionMarketStatus.Committed || result.Operation?.State != AuctionOperationState.Committed) return;
        TryProjectHistory(() => history.Observe(new[] { result.Operation }));
        using var json = JsonDocument.Parse(result.Operation.Intent.IntentJson);
        var transition = AuctionOrderTransition.DeserializeStrict(json.RootElement.GetProperty("orderTransition").GetString());
        var recipients = transition.Legs.Select(leg => leg.Recipient.CharacterId).Distinct().ToArray();
        if (recipients.Length > 0) DeliveriesCommitted?.Invoke(recipients);
    }

    private AuctionOrderRecoveryResult RecoverQuiescent(int limit=1000)
    {
        var pending = Store.Pending(limit);
        var result = recovery.RecoverPendingAfterExecutorsQuiesced(limit);
        foreach (var old in pending)
        {
            var terminal = Store.FindOperation(old.Intent.OperationId);
            if (terminal?.State == AuctionOperationState.Committed)
                PublishCommitted(new(AuctionMarketStatus.Committed, "", null, terminal));
        }
        return result;
    }

    private void ExpireDue()
    { foreach (var result in expiry.RunDue(now(), 128)) PublishCommitted(result); }

    // Pending/quarantined holes are revisited independently of the forward scan.
    // A late commit below the scan cursor must still appear exactly once.
    internal void SynchronizeHistory()
    {
        TryProjectHistory(() => {
            foreach (var id in history.ReadPending())
            {
                var entry = Store.FindOperation(id) ?? throw new InvalidOperationException("History source operation disappeared.");
                history.Observe(new[] { entry });
            }
            for (int page = 0; page < 4; page++)
            {
                var rows = Store.ReadHistorySource(history.ReadCheckpoint());
                history.Observe(rows, advanceSource: true);
                if (rows.Count < 256) break;
            }
        });
    }

    private static void TryProjectHistory(Action project)
    {
        try { project(); }
        catch (Exception ex) when (ex is IOException || ex is Microsoft.Data.Sqlite.SqliteException
            || ex is InvalidDataException || ex is InvalidOperationException || ex is ArgumentException || ex is JsonException
            || ex is AuctionItemCodecException || ex is FormatException || ex is OverflowException)
        {
            // The authoritative journal remains durable. The next existing
            // recovery/expiry tick replays it; never debit or deliver again.
            FileLogger.Log("[AuctionHistory] Projection pending: " + ex.GetType().Name);
        }
    }

    private AuctionMarketEntitlements ReadEntitlements(AuctionMarketRequest request,
        AuctionOrderSnapshot before, long timestamp)
    {
        if (request.Action != AuctionOperationAction.List)
            return new(null, 0, before?.UnitPriceGold.HasValue == true);
        var rights = publicDatabase.Read(c => AuctionEntitlementReader.Read(c, null, request.Actor.AccountId, timestamp));
        return new(rights.ExpiresAtUnix, Store.CountActiveAndReservedAccountListings(request.Actor.AccountId),
            request.UnitPriceGold.HasValue);
    }

    private MailboxSendRequest CreateDeliveryMail(AuctionDeliveryLeg leg, Guid operation)
    {
        // A real recipient owns both the public receipt and each system-mail leg.
        // No synthetic account/character is needed for offline deliveries.
        var mail = new MailboxSendRequest {
            SenderCharacterId = leg.Recipient.CharacterId, SenderAccountId = leg.Recipient.AccountId,
            SenderName = "拍卖行", ReceiverCharacterId = leg.Recipient.CharacterId,
            ReceiverAccountId = leg.Recipient.AccountId, ReceiverName = leg.Recipient.FrozenName,
            Gold = checked((int)leg.Gold), Title = "拍卖行", Text = DeliveryText(leg.Kind),
            MailType = 1, Unlimited = true, AuditActor = "auction", AuditReason = leg.Kind.ToString(),
            IdempotencyKey = "auction:" + operation.ToString("N") + ":" + leg.LegKey
        };
        if (leg.ItemEnvelopeJson != null)
        {
            var item = Codec.DeserializeStrict(leg.ItemEnvelopeJson);
            mail.Attachments = new[] { new MailboxSendAttachmentRequest {
                ItemId = Codec.DecodeStrict(item).ItemId, ItemCount = leg.Quantity,
                ItemCoreData = item.NativeCore99, DetailJson = item.DetailJson, ExtraJson = item.ExtensionsJson
            } };
        }
        return mail;
    }

    private static string DeliveryText(AuctionDeliveryKind kind) => kind switch {
        AuctionDeliveryKind.ItemDelivery => "您购买的物品已送达。",
        AuctionDeliveryKind.ItemReturn => "您的拍卖物品已退回。",
        AuctionDeliveryKind.SellerProceeds => "您的拍卖成交款已到账（已扣除手续费）。",
        AuctionDeliveryKind.BidRefund => "您的竞拍款已退回。",
        AuctionDeliveryKind.DepositRefund => "您的拍卖押金已退回。",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // Called before accepting packets; all previous-process executors are absent.
    internal AuctionOrderRecoveryResult Start(ClockService clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (Gate)
        {
            if (disposed || timer != null) throw new InvalidOperationException("Auction runtime already started or disposed.");
            var result = RecoverQuiescent();
            ExpireDue();
            SynchronizeHistory();
            if (requestedEntryEnabled)
            {
                try { _ = catalog.Value; EntryEnabled = true; }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException
                    || ex is InvalidOperationException || ex is ArgumentException)
                { FileLogger.Log("[Auction] Entry unavailable: " + ex.GetType().Name); }
            }
            Schedule(clock);
            return result;
        }
    }

    private void Schedule(ClockService clock)
    {
        timer = clock.ScheduleOneShotAfter("auction:funded-expiry", TimeSpan.FromMinutes(1), _ => {
            lock (Gate)
            {
                if (disposed) return;
                // All market/public executors are synchronous under Gate. Once
                // this owner acquires it, no previous attempt can still commit.
                try { RecoverQuiescent(); ExpireDue(); SynchronizeHistory(); }
                finally { if (!disposed) Schedule(clock); }
            }
        });
    }

    public void Dispose()
    {
        lock (Gate) { disposed = true; timer?.Cancel(); timer = null; DeliveriesCommitted = null; }
    }
}
