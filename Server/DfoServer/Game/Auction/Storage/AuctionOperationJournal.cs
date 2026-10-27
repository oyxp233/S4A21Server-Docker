using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Storage
{
    // Only owns durable intents/reservations. It is not an order store, wallet,
    // or proof that an asset debit happened. Future order CAS must share the
    // private transaction with PrepareInTransaction/ResolveInTransaction.
    internal sealed class AuctionOperationJournal
    {
        private readonly IGameDatabase database;
        internal AuctionOperationJournal(string privatePath)
            => database = AuctionDatabase.Open(privatePath);

        internal AuctionJournalEntry Find(Guid id) => database.Read(c => Find(c, null, id));

        internal static AuctionJournalEntry FindInTransaction(SqliteConnection c, SqliteTransaction t, Guid id)
        {
            RequireTransaction(c, t);
            return Find(c, t, id);
        }

        internal AuctionJournalEntry Prepare(AuctionOperationIntent intent)
            => database.Write((c, t) => PrepareInTransaction(c, t, intent));

        internal IReadOnlyList<AuctionJournalEntry> Pending(long afterSequence, int limit)
        {
            if (afterSequence < 0 || limit < 1 || limit > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit));
            return database.Read(c =>
            {
                using var cmd = Command(c, null, Select +
                    " WHERE state=0 AND sequence_id>@after ORDER BY sequence_id LIMIT @limit;");
                cmd.Parameters.AddWithValue("@after", afterSequence);
                cmd.Parameters.AddWithValue("@limit", limit);
                using var reader = cmd.ExecuteReader();
                var result = new List<AuctionJournalEntry>();
                while (reader.Read()) result.Add(Read(reader));
                return result;
            });
        }

        // Caller must hold the same execution gate as asset commit/recovery.
        // This method does not interpret a timeout or unavailable DB as absence.
        internal AuctionJournalEntry AbortVerifiedUncommitted(AuctionOperationIntent intent, string reason)
            => Resolve(intent, AuctionOperationState.Aborted, "{}", reason);

        internal AuctionJournalEntry Complete(AuctionOperationIntent intent, string resultJson)
            => Resolve(intent, AuctionOperationState.Committed, resultJson, "");

        internal AuctionJournalEntry Quarantine(AuctionOperationIntent intent, string reason)
            => Resolve(intent, AuctionOperationState.Quarantined, "{}", reason);

        private AuctionJournalEntry Resolve(AuctionOperationIntent intent, AuctionOperationState state,
            string resultJson, string reason)
            => database.Write((c, t) => ResolveInTransaction(c, t, intent, state, resultJson, reason));

        internal static AuctionJournalEntry PrepareInTransaction(SqliteConnection c, SqliteTransaction t,
            AuctionOperationIntent intent)
        {
            RequireTransaction(c, t);
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            var existing = Find(c, t, intent.OperationId);
            if (existing != null) { existing.Intent.RequireSame(intent); return existing; }
            using var cmd = Command(c, t, @"
INSERT INTO auction_operation(operation_id,resource_key,action,character_id,account_id,
    expected_version,created_at_unix,intent_json,intent_hash)
VALUES(@id,@resource,@action,@cid,@aid,@version,@created,@json,@hash);");
            cmd.Parameters.AddWithValue("@id", intent.OperationId.ToString("N"));
            cmd.Parameters.AddWithValue("@resource", intent.ResourceKey);
            cmd.Parameters.AddWithValue("@action", (int)intent.Action);
            cmd.Parameters.AddWithValue("@cid", intent.CharacterId);
            cmd.Parameters.AddWithValue("@aid", intent.AccountId);
            cmd.Parameters.AddWithValue("@version", intent.ExpectedVersion);
            cmd.Parameters.AddWithValue("@created", intent.CreatedAtUnix);
            cmd.Parameters.AddWithValue("@json", intent.IntentJson);
            cmd.Parameters.AddWithValue("@hash", intent.IntentHash);
            cmd.ExecuteNonQuery(); // Partial unique index is the durable reservation.
            return Find(c, t, intent.OperationId);
        }

        internal static AuctionJournalEntry ResolveInTransaction(SqliteConnection c, SqliteTransaction t,
            AuctionOperationIntent intent, AuctionOperationState state, string resultJson, string reason)
        {
            RequireTransaction(c, t);
            if (intent == null || state == AuctionOperationState.Prepared || !Enum.IsDefined(state))
                throw new ArgumentException("Invalid auction journal resolution.");
            AuctionOperationIntent.RequireObject(resultJson);
            if (reason == null || (state != AuctionOperationState.Committed && string.IsNullOrWhiteSpace(reason)))
                throw new ArgumentException("A failed resolution requires a reason.");
            var existing = Find(c, t, intent.OperationId)
                ?? throw new InvalidOperationException("Auction operation was not prepared.");
            existing.Intent.RequireSame(intent);
            if (existing.State != AuctionOperationState.Prepared)
            {
                if (existing.State != state || existing.ResultJson != resultJson || existing.ResolutionReason != reason)
                    throw new InvalidOperationException("Auction terminal result conflict.");
                return existing;
            }
            using var cmd = Command(c, t, @"
UPDATE auction_operation SET state=@state,result_json=@result,resolution_reason=@reason
WHERE operation_id=@id AND intent_hash=@hash AND state=0;");
            cmd.Parameters.AddWithValue("@state", (int)state);
            cmd.Parameters.AddWithValue("@result", resultJson);
            cmd.Parameters.AddWithValue("@reason", reason);
            cmd.Parameters.AddWithValue("@id", intent.OperationId.ToString("N"));
            cmd.Parameters.AddWithValue("@hash", intent.IntentHash);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Auction reservation changed.");
            return Find(c, t, intent.OperationId);
        }

        private const string Select = @"SELECT sequence_id,operation_id,resource_key,action,character_id,
account_id,expected_version,created_at_unix,intent_json,intent_hash,state,result_json,resolution_reason
FROM auction_operation";
        private static AuctionJournalEntry Find(SqliteConnection c, SqliteTransaction t, Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("Missing auction operation ID.");
            using var cmd = Command(c, t, Select + " WHERE operation_id=@id;");
            cmd.Parameters.AddWithValue("@id", id.ToString("N"));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }

        private static AuctionJournalEntry Read(SqliteDataReader r)
        {
            var intent = new AuctionOperationIntent(Guid.ParseExact(r.GetString(1), "N"), r.GetString(2),
                (AuctionOperationAction)r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.GetInt64(6), r.GetInt64(7), r.GetString(8));
            if (intent.IntentHash != r.GetString(9)) throw new InvalidOperationException("Corrupt auction intent hash.");
            var state = (AuctionOperationState)r.GetInt32(10);
            if (!Enum.IsDefined(state)) throw new InvalidOperationException("Corrupt auction journal state.");
            var result = r.GetString(11); AuctionOperationIntent.RequireObject(result);
            return new AuctionJournalEntry(r.GetInt64(0), intent, state, result, r.GetString(12));
        }

        private static SqliteCommand Command(SqliteConnection c, SqliteTransaction t, string sql)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; return cmd;
        }

        private static void RequireTransaction(SqliteConnection c, SqliteTransaction t)
        {
            if (c == null || t == null || !ReferenceEquals(t.Connection, c))
                throw new ArgumentException("Auction journal mutation requires the matching private transaction.");
        }
    }
}
