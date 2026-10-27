using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Queries;

// A rebuildable projection of the durable journal, never a balance/escrow owner.
// Source observations, sale rows and scan progress commit together in this file.
internal sealed class SqliteAuctionTradeHistory : IAuctionTradeHistoryReader
{
    private const string Baseline = "a21-auction-history-v1";
    private readonly string connectionString;
    private readonly AuctionItemCodec codec;
    internal string DatabasePath { get; }
    internal sealed record Checkpoint(long Sequence, string OperationId, string IntentHash);

    internal SqliteAuctionTradeHistory(string path, AuctionItemCodec codec)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("History path required.");
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        DatabasePath = Path.GetFullPath(path);
        bool existed = File.Exists(DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath));
        connectionString = new SqliteConnectionStringBuilder {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) {
            Mode = existed ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        c.Open();
        using var t = c.BeginTransaction(deferred: existed);
        using var check = Command(c, t, "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';");
        if (!existed && Convert.ToInt32(check.ExecuteScalar()) == 0)
        {
            using var create = Command(c, t, Schema);
            create.ExecuteNonQuery();
        }
        using var version = Command(c, t, "PRAGMA user_version;");
        if (Convert.ToInt32(version.ExecuteScalar()) != 1) throw new InvalidDataException("Unknown auction history version.");
        using var metadata = Command(c, t, "SELECT baseline_id FROM history_metadata WHERE singleton=1;");
        if ((string)metadata.ExecuteScalar() != Baseline) throw new InvalidDataException("Unknown auction history baseline.");
        var expected = Schema.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
            .Where(s => s.StartsWith("CREATE ", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        using var objects = Command(c, t, "SELECT sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (var r = objects.ExecuteReader()) while (r.Read()) actual.Add(r.GetString(0).Trim());
        if (!expected.SetEquals(actual)) throw new InvalidDataException("Auction history schema mismatch.");
        t.Commit();
    }

    internal Checkpoint ReadCheckpoint()
    {
        using var c = Open();
        using var cmd = Command(c, null, @"SELECT m.source_sequence,s.operation_id,s.intent_hash
FROM history_metadata m LEFT JOIN history_sources s ON s.sequence_id=m.source_sequence WHERE m.singleton=1;");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("Missing history metadata.");
        return new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    internal IReadOnlyList<Guid> ReadPending(int limit = 256)
    {
        RequireLimit(limit, 256);
        using var c = Open();
        using var t = c.BeginTransaction(deferred: false);
        using var cmd = Command(c, t, @"SELECT sequence_id,operation_id FROM history_sources
WHERE state IN (0,3) AND sequence_id>(SELECT pending_after FROM history_metadata WHERE singleton=1)
ORDER BY sequence_id LIMIT @limit;");
        cmd.Parameters.AddWithValue("@limit", limit);
        var ids = new List<Guid>(); long last = 0;
        using (var r = cmd.ExecuteReader()) while (r.Read()) { last = r.GetInt64(0); ids.Add(Guid.ParseExact(r.GetString(1), "N")); }
        using var next = Command(c, t, "UPDATE history_metadata SET pending_after=@after WHERE singleton=1;");
        next.Parameters.AddWithValue("@after", ids.Count == limit ? last : 0); next.ExecuteNonQuery();
        t.Commit(); return ids;
    }

    internal void Observe(IReadOnlyList<AuctionJournalEntry> entries, bool advanceSource = false)
    {
        if (entries == null || entries.Count > 1000) throw new ArgumentException("Bounded history source batch required.");
        using var c = Open();
        using var t = c.BeginTransaction(deferred: false);
        long last = 0;
        foreach (var entry in entries)
        {
            if (entry == null || entry.SequenceId <= 0 || !Enum.IsDefined(entry.State)
                || (advanceSource && entry.SequenceId <= last)) throw new InvalidDataException("Invalid history source order.");
            string id = entry.Intent.OperationId.ToString("N"), resultHash = Hash(entry.ResultJson);
            using var prior = Command(c, t, "SELECT operation_id,intent_hash,state,result_hash FROM history_sources WHERE sequence_id=@seq;");
            prior.Parameters.AddWithValue("@seq", entry.SequenceId);
            using (var r = prior.ExecuteReader())
            {
                if (r.Read())
                {
                    var state = (AuctionOperationState)r.GetInt32(2);
                    if (r.GetString(0) != id || r.GetString(1) != entry.Intent.IntentHash
                        || (state == AuctionOperationState.Quarantined && entry.State == AuctionOperationState.Prepared)
                        || (state is AuctionOperationState.Committed or AuctionOperationState.Aborted
                            && (state != entry.State || r.GetString(3) != resultHash)))
                        throw new InvalidDataException("History source identity or terminal result changed.");
                }
            }
            using var put = Command(c, t, @"INSERT INTO history_sources(sequence_id,operation_id,intent_hash,state,result_hash)
VALUES(@seq,@id,@hash,@state,@result) ON CONFLICT(sequence_id) DO UPDATE SET state=excluded.state,result_hash=excluded.result_hash;");
            put.Parameters.AddWithValue("@seq", entry.SequenceId); put.Parameters.AddWithValue("@id", id);
            put.Parameters.AddWithValue("@hash", entry.Intent.IntentHash); put.Parameters.AddWithValue("@state", (int)entry.State);
            put.Parameters.AddWithValue("@result", resultHash); put.ExecuteNonQuery();
            if (entry.State == AuctionOperationState.Committed) InsertSale(c, t, entry);
            last = entry.SequenceId;
        }
        if (advanceSource && entries.Count > 0)
        {
            using var cursor = Command(c, t, "UPDATE history_metadata SET source_sequence=max(source_sequence,@seq) WHERE singleton=1;");
            cursor.Parameters.AddWithValue("@seq", last); cursor.ExecuteNonQuery();
        }
        t.Commit();
    }

    private void InsertSale(SqliteConnection c, SqliteTransaction t, AuctionJournalEntry entry)
    {
        if (entry.Intent.Action is not (AuctionOperationAction.Buyout or AuctionOperationAction.BuyPiece or AuctionOperationAction.Expire)) return;
        using var json = JsonDocument.Parse(entry.Intent.IntentJson);
        var transition = AuctionOrderTransition.DeserializeStrict(json.RootElement.GetProperty("orderTransition").GetString());
        var before = transition.Before; var after = transition.After;
        var deliveries = transition.Legs.Where(l => l.Kind == AuctionDeliveryKind.ItemDelivery).ToArray();
        if (entry.Intent.Action == AuctionOperationAction.Expire && deliveries.Length == 0) return;
        if (before == null || deliveries.Length != 1) throw new InvalidDataException("Invalid sale delivery.");
        var leg = deliveries[0]; long gross = checked(after.CumulativeGrossGold - before.CumulativeGrossGold);
        long fee = checked(after.CumulativeFeeGold - before.CumulativeFeeGold);
        int quantity = checked(before.RemainingQuantity - after.RemainingQuantity);
        if (quantity <= 0 || leg.Quantity != quantity || gross <= 0 || fee < 0 || fee > gross
            || after.OrderId != before.OrderId || after.Version != checked(before.Version + 1) || after.Seller != before.Seller
            || entry.Intent.ExpectedVersion != before.Version
            || transition.Quote.GrossGold != gross || transition.Quote.SettlementFeeGold != fee
            || leg.Recipient == null || leg.Recipient.AccountId <= 0 || leg.Recipient.CharacterId <= 0
            || leg.Recipient.AccountId == before.Seller.AccountId
            || (entry.Intent.Action == AuctionOperationAction.Expire
                ? before.HighestBidder != leg.Recipient || before.FundedBidGold != gross
                : entry.Intent.AccountId != leg.Recipient.AccountId || entry.Intent.CharacterId != leg.Recipient.CharacterId))
            throw new InvalidDataException("Invalid committed sale totals.");
        var core = codec.DecodeStrict(codec.DeserializeStrict(leg.ItemEnvelopeJson));
        using var cmd = Command(c, t, @"INSERT INTO history_sales(operation_id,sequence_id,order_id,completed_at_unix,
seller_account_id,seller_character_id,buyer_account_id,buyer_character_id,item_id,quantity,gross_gold,fee_gold)
VALUES(@op,@seq,@order,@time,@sa,@sc,@ba,@bc,@item,@qty,@gross,@fee) ON CONFLICT(operation_id) DO NOTHING;");
        cmd.Parameters.AddWithValue("@op", entry.Intent.OperationId.ToString("N")); cmd.Parameters.AddWithValue("@seq", entry.SequenceId);
        cmd.Parameters.AddWithValue("@order", after.OrderId); cmd.Parameters.AddWithValue("@time", entry.Intent.CreatedAtUnix);
        cmd.Parameters.AddWithValue("@sa", before.Seller.AccountId); cmd.Parameters.AddWithValue("@sc", before.Seller.CharacterId);
        cmd.Parameters.AddWithValue("@ba", leg.Recipient.AccountId); cmd.Parameters.AddWithValue("@bc", leg.Recipient.CharacterId);
        cmd.Parameters.AddWithValue("@item", core.ItemId); cmd.Parameters.AddWithValue("@qty", quantity);
        cmd.Parameters.AddWithValue("@gross", gross); cmd.Parameters.AddWithValue("@fee", fee); cmd.ExecuteNonQuery();
    }

    public AuctionTradeHistoryPage Read(AuctionTradeHistoryQuery query)
    {
        if (query == null || query.AccountId <= 0 || query.CharacterId <= 0) throw new ArgumentException("Authenticated history scope required.");
        RequireLimit(query.Limit, 100);
        long upper = long.MaxValue;
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            var parts = query.Cursor.Split(':');
            if (parts.Length != 4 || parts[0] != "v1" || parts[1] != query.AccountId.ToString(CultureInfo.InvariantCulture)
                || parts[2] != query.CharacterId.ToString(CultureInfo.InvariantCulture)
                || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out upper) || upper <= 0)
                throw new ArgumentException("Invalid or cross-scope history cursor.");
        }
        using var c = Open();
        using var cmd = Command(c, null, @"SELECT sequence_id,order_id,completed_at_unix,item_id,quantity,gross_gold,fee_gold,
CASE WHEN seller_account_id=@aid AND seller_character_id=@cid THEN 1 ELSE 0 END
FROM history_sales WHERE sequence_id<@upper AND
((seller_account_id=@aid AND seller_character_id=@cid) OR (buyer_account_id=@aid AND buyer_character_id=@cid))
ORDER BY sequence_id DESC LIMIT @limit;");
        cmd.Parameters.AddWithValue("@aid", query.AccountId); cmd.Parameters.AddWithValue("@cid", query.CharacterId);
        cmd.Parameters.AddWithValue("@upper", upper); cmd.Parameters.AddWithValue("@limit", query.Limit + 1);
        var rows = new List<AuctionTradeHistoryEntry>(); long last = 0; bool more = false;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (rows.Count == query.Limit) { more = true; break; }
            last = r.GetInt64(0);
            rows.Add(new(r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7) == 1));
        }
        string next = more ? FormattableString.Invariant($"v1:{query.AccountId}:{query.CharacterId}:{last}") : null;
        return new(rows.AsReadOnly(), next);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString); c.Open();
        using var cmd = Command(c, null, "PRAGMA busy_timeout=5000;"); cmd.ExecuteNonQuery(); return c;
    }
    private static void RequireLimit(int value, int max) { if (value < 1 || value > max) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction t, string sql)
    { var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; return cmd; }
    private const string Schema = @"
CREATE TABLE history_metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1),baseline_id TEXT NOT NULL,
source_sequence INTEGER NOT NULL CHECK(source_sequence>=0),pending_after INTEGER NOT NULL CHECK(pending_after>=0));
INSERT INTO history_metadata VALUES(1,'a21-auction-history-v1',0,0);
CREATE TABLE history_sources(sequence_id INTEGER PRIMARY KEY CHECK(sequence_id>0),operation_id TEXT NOT NULL UNIQUE,
intent_hash TEXT NOT NULL,state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),result_hash TEXT NOT NULL);
CREATE INDEX history_sources_pending ON history_sources(state,sequence_id);
CREATE TABLE history_sales(operation_id TEXT PRIMARY KEY,sequence_id INTEGER NOT NULL UNIQUE,order_id INTEGER NOT NULL,
completed_at_unix INTEGER NOT NULL,seller_account_id INTEGER NOT NULL,seller_character_id INTEGER NOT NULL,
buyer_account_id INTEGER NOT NULL,buyer_character_id INTEGER NOT NULL,item_id INTEGER NOT NULL,
quantity INTEGER NOT NULL CHECK(quantity>0),gross_gold INTEGER NOT NULL CHECK(gross_gold>0),fee_gold INTEGER NOT NULL CHECK(fee_gold>=0 AND fee_gold<=gross_gold));
CREATE INDEX history_sales_seller ON history_sales(seller_account_id,seller_character_id,sequence_id DESC);
CREATE INDEX history_sales_buyer ON history_sales(buyer_account_id,buyer_character_id,sequence_id DESC);
PRAGMA user_version=1;";
}
