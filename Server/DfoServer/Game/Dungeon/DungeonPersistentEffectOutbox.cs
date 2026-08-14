using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonPersistentEffectState
    {
        Pending = 0,
        Reserved = 1,
        Committed = 2,
        Failed = 3,
        DeadLetter = 4,
    }

    internal enum DungeonPersistentEffectClaimResult
    {
        Missing = 0,
        Claimed = 1,
        Busy = 2,
        Committed = 3,
        DeadLetter = 4,
    }

    internal sealed class DungeonPersistentEffectDefinition
    {
        internal DungeonEffectId EffectId { get; set; }
        internal int CharacterId { get; set; }
        internal int AccountId { get; set; }
        internal int PayloadVersion { get; set; }
        internal string PayloadJson { get; set; }
    }

    internal sealed class DungeonPersistentEffectRecord
    {
        internal DungeonEffectId EffectId { get; set; }
        internal int CharacterId { get; set; }
        internal int AccountId { get; set; }
        internal int PayloadVersion { get; set; }
        internal string PayloadJson { get; set; }
        internal DungeonPersistentEffectState State { get; set; }
        internal Guid LeaseId { get; set; }
        internal string LeaseOwner { get; set; }
        internal long LeaseExpiresAt { get; set; }
        internal int AttemptCount { get; set; }
        internal string LastError { get; set; }
        internal int? ResultVersion { get; set; }
        internal string ResultJson { get; set; }
        internal long CreatedAt { get; set; }
        internal long UpdatedAt { get; set; }
        internal long? CommittedAt { get; set; }
    }

    internal readonly struct DungeonPersistentEffectRecoveryCursor
    {
        internal DungeonPersistentEffectRecoveryCursor(
            long createdAt,
            string effectKind,
            DungeonEffectScope effectScope,
            long scopeTarget,
            Guid sourceEventId)
        {
            CreatedAt = createdAt;
            EffectKind = effectKind;
            EffectScope = effectScope;
            ScopeTarget = scopeTarget;
            SourceEventId = sourceEventId;
        }

        internal long CreatedAt { get; }
        internal string EffectKind { get; }
        internal DungeonEffectScope EffectScope { get; }
        internal long ScopeTarget { get; }
        internal Guid SourceEventId { get; }
        internal bool IsValid => CreatedAt >= 0
            && !string.IsNullOrWhiteSpace(EffectKind)
            && SourceEventId != Guid.Empty;

        internal static DungeonPersistentEffectRecoveryCursor From(
            DungeonPersistentEffectRecord record)
            => record == null
                ? default
                : new DungeonPersistentEffectRecoveryCursor(
                    record.CreatedAt,
                    record.EffectId.EffectKind,
                    record.EffectId.Scope,
                    record.EffectId.ScopeTarget,
                    record.EffectId.SourceEventId);
    }

    internal readonly struct DungeonPersistentEffectReservation
    {
        internal DungeonPersistentEffectReservation(
            DungeonEffectId effectId,
            Guid leaseId,
            string leaseOwner)
        {
            EffectId = effectId;
            LeaseId = leaseId;
            LeaseOwner = leaseOwner;
        }

        internal DungeonEffectId EffectId { get; }
        internal Guid LeaseId { get; }
        internal string LeaseOwner { get; }
        internal bool IsValid => LeaseId != Guid.Empty
            && !string.IsNullOrWhiteSpace(LeaseOwner);
    }

    // Durable idempotency/recovery store for typed dungeon effects. It does
    // not execute arbitrary delegates and does not treat packet sends as ACKed.
    internal sealed class DungeonPersistentEffectOutbox
    {
        private const int MaximumErrorLength = 2000;
        private static readonly Guid ProcessOwnerId = Guid.NewGuid();

        private readonly string _connectionString;
        private readonly string _leaseOwner;
        private readonly Func<long> _utcNowMilliseconds;

        internal DungeonPersistentEffectOutbox(
            string connectionString,
            Guid? leaseOwner = null,
            Func<long> utcNowMilliseconds = null)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException(
                    "A database connection string is required.",
                    nameof(connectionString));
            _leaseOwner = (leaseOwner ?? ProcessOwnerId).ToString("N");
            _utcNowMilliseconds = utcNowMilliseconds
                ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ReleaseForeignProcessReservations();
        }

        internal string LeaseOwner => _leaseOwner;
        internal long UtcNowMilliseconds => _utcNowMilliseconds();

        internal bool Enqueue(DungeonPersistentEffectDefinition definition)
        {
            ValidateDefinition(definition);
            var now = _utcNowMilliseconds();
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var inserted = InsertPending(
                    connection,
                    transaction,
                    definition,
                    now);
                var stored = Load(
                    connection,
                    transaction,
                    definition.EffectId);
                if (stored == null)
                    throw new InvalidOperationException(
                        "Persistent dungeon effect disappeared after enqueue.");
                EnsureDefinitionMatches(stored, definition);
                transaction.Commit();
                return inserted;
            }
        }

        internal DungeonPersistentEffectClaimResult TryClaim(
            DungeonEffectId effectId,
            TimeSpan leaseDuration,
            out DungeonPersistentEffectReservation reservation,
            out DungeonPersistentEffectRecord record)
        {
            reservation = default;
            record = null;
            if (leaseDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(leaseDuration));

            var now = _utcNowMilliseconds();
            var leaseMilliseconds = (long)Math.Ceiling(
                leaseDuration.TotalMilliseconds);
            if (leaseMilliseconds <= 0)
                leaseMilliseconds = 1;

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                record = Load(connection, transaction, effectId);
                if (record == null)
                    return DungeonPersistentEffectClaimResult.Missing;
                if (record.State == DungeonPersistentEffectState.Committed)
                    return DungeonPersistentEffectClaimResult.Committed;
                if (record.State == DungeonPersistentEffectState.DeadLetter)
                    return DungeonPersistentEffectClaimResult.DeadLetter;
                if (record.State == DungeonPersistentEffectState.Reserved
                    && record.LeaseExpiresAt > now)
                {
                    return DungeonPersistentEffectClaimResult.Busy;
                }

                var leaseId = Guid.NewGuid();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
UPDATE dungeon_persistent_effect_outbox
SET state = @reserved,
    lease_id = @leaseId,
    lease_owner = @leaseOwner,
    lease_expires_at = @leaseExpiresAt,
    attempt_count = attempt_count + 1,
    last_error = '',
    updated_at = @now
WHERE source_event_id = @eventId
  AND effect_kind = @effectKind
  AND effect_scope = @effectScope
  AND scope_target = @scopeTarget
  AND state IN (@pending, @reserved, @failed);";
                    AddIdentityParameters(command, effectId);
                    command.Parameters.AddWithValue(
                        "@pending",
                        (int)DungeonPersistentEffectState.Pending);
                    command.Parameters.AddWithValue(
                        "@reserved",
                        (int)DungeonPersistentEffectState.Reserved);
                    command.Parameters.AddWithValue(
                        "@failed",
                        (int)DungeonPersistentEffectState.Failed);
                    command.Parameters.AddWithValue(
                        "@leaseId",
                        leaseId.ToString("N"));
                    command.Parameters.AddWithValue("@leaseOwner", _leaseOwner);
                    command.Parameters.AddWithValue(
                        "@leaseExpiresAt",
                        AddSaturating(now, leaseMilliseconds));
                    command.Parameters.AddWithValue("@now", now);
                    if (command.ExecuteNonQuery() != 1)
                        return DungeonPersistentEffectClaimResult.Busy;
                }

                transaction.Commit();
                reservation = new DungeonPersistentEffectReservation(
                    effectId,
                    leaseId,
                    _leaseOwner);
                record.State = DungeonPersistentEffectState.Reserved;
                record.LeaseId = leaseId;
                record.LeaseOwner = _leaseOwner;
                record.LeaseExpiresAt = AddSaturating(now, leaseMilliseconds);
                record.AttemptCount++;
                record.UpdatedAt = now;
                return DungeonPersistentEffectClaimResult.Claimed;
            }
        }

        internal bool TryCommit(
            DungeonPersistentEffectReservation reservation,
            int resultVersion,
            string resultJson)
        {
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                if (!TryCommitInTransaction(
                        connection,
                        transaction,
                        reservation,
                        resultVersion,
                        resultJson,
                        _utcNowMilliseconds()))
                {
                    return false;
                }

                transaction.Commit();
                return true;
            }
        }

        internal bool TryCommitInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DungeonPersistentEffectReservation reservation,
            int resultVersion,
            string resultJson,
            long committedAt)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (!reservation.IsValid
                || resultVersion <= 0
                || string.IsNullOrWhiteSpace(resultJson))
            {
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE dungeon_persistent_effect_outbox
SET state = @committed,
    lease_id = NULL,
    lease_owner = NULL,
    lease_expires_at = 0,
    last_error = '',
    result_version = @resultVersion,
    result_json = @resultJson,
    updated_at = @committedAt,
    committed_at = @committedAt
WHERE source_event_id = @eventId
  AND effect_kind = @effectKind
  AND effect_scope = @effectScope
  AND scope_target = @scopeTarget
  AND state = @reserved
  AND lease_id = @leaseId
  AND lease_owner = @leaseOwner;";
                AddIdentityParameters(command, reservation.EffectId);
                command.Parameters.AddWithValue(
                    "@committed",
                    (int)DungeonPersistentEffectState.Committed);
                command.Parameters.AddWithValue(
                    "@reserved",
                    (int)DungeonPersistentEffectState.Reserved);
                command.Parameters.AddWithValue(
                    "@leaseId",
                    reservation.LeaseId.ToString("N"));
                command.Parameters.AddWithValue(
                    "@leaseOwner",
                    reservation.LeaseOwner);
                command.Parameters.AddWithValue("@resultVersion", resultVersion);
                command.Parameters.AddWithValue("@resultJson", resultJson);
                command.Parameters.AddWithValue("@committedAt", committedAt);
                return command.ExecuteNonQuery() == 1;
            }
        }

        internal bool TryFail(
            DungeonPersistentEffectReservation reservation,
            string error)
            => TryFinishReservation(
                reservation,
                DungeonPersistentEffectState.Failed,
                error);

        internal bool TryDeadLetter(
            DungeonPersistentEffectReservation reservation,
            string error)
            => TryFinishReservation(
                reservation,
                DungeonPersistentEffectState.DeadLetter,
                error);

        internal DungeonPersistentEffectRecord Get(DungeonEffectId effectId)
        {
            using (var connection = Open())
                return Load(connection, transaction: null, effectId);
        }

        internal IReadOnlyList<DungeonPersistentEffectRecord>
            LoadRecoverableForCharacter(int characterId, int maximumCount = 64)
            => LoadRecoverableForCharacter(
                characterId,
                after: null,
                maximumCount);

        internal IReadOnlyList<DungeonPersistentEffectRecord>
            LoadRecoverableForCharacter(
                int characterId,
                DungeonPersistentEffectRecoveryCursor? after,
                int maximumCount)
        {
            var result = new List<DungeonPersistentEffectRecord>();
            if (characterId <= 0 || maximumCount <= 0)
                return result;
            var now = _utcNowMilliseconds();
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT source_event_id, effect_kind, effect_scope, scope_target,
       character_id, account_id, payload_version, payload_json,
       state, lease_id, lease_owner, lease_expires_at, attempt_count,
       last_error, result_version, result_json,
       created_at, updated_at, committed_at
FROM dungeon_persistent_effect_outbox
WHERE character_id = @characterId
  AND (state IN (@pending, @failed)
       OR (state = @reserved AND lease_expires_at <= @now))
  AND (@hasCursor = 0
       OR created_at > @cursorCreatedAt
       OR (created_at = @cursorCreatedAt
           AND effect_kind > @cursorEffectKind)
       OR (created_at = @cursorCreatedAt
           AND effect_kind = @cursorEffectKind
           AND effect_scope > @cursorEffectScope)
       OR (created_at = @cursorCreatedAt
           AND effect_kind = @cursorEffectKind
           AND effect_scope = @cursorEffectScope
           AND scope_target > @cursorScopeTarget)
       OR (created_at = @cursorCreatedAt
           AND effect_kind = @cursorEffectKind
           AND effect_scope = @cursorEffectScope
           AND scope_target = @cursorScopeTarget
           AND source_event_id > @cursorSourceEventId))
ORDER BY created_at, effect_kind, effect_scope, scope_target, source_event_id
LIMIT @maximumCount;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue(
                    "@pending",
                    (int)DungeonPersistentEffectState.Pending);
                command.Parameters.AddWithValue(
                    "@reserved",
                    (int)DungeonPersistentEffectState.Reserved);
                command.Parameters.AddWithValue(
                    "@failed",
                    (int)DungeonPersistentEffectState.Failed);
                command.Parameters.AddWithValue("@now", now);
                var cursor = after.GetValueOrDefault();
                var hasCursor = after.HasValue && cursor.IsValid;
                command.Parameters.AddWithValue(
                    "@hasCursor",
                    hasCursor ? 1 : 0);
                command.Parameters.AddWithValue(
                    "@cursorCreatedAt",
                    hasCursor ? cursor.CreatedAt : 0);
                command.Parameters.AddWithValue(
                    "@cursorEffectKind",
                    hasCursor ? cursor.EffectKind : string.Empty);
                command.Parameters.AddWithValue(
                    "@cursorEffectScope",
                    hasCursor ? (int)cursor.EffectScope : 0);
                command.Parameters.AddWithValue(
                    "@cursorScopeTarget",
                    hasCursor ? cursor.ScopeTarget : 0);
                command.Parameters.AddWithValue(
                    "@cursorSourceEventId",
                    hasCursor
                        ? cursor.SourceEventId.ToString("N")
                        : string.Empty);
                command.Parameters.AddWithValue("@maximumCount", maximumCount);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(ReadRecord(reader));
                }
            }
            return result;
        }

        internal int CountRecoverableForCharacter(int characterId)
        {
            if (characterId <= 0)
                return 0;

            var now = _utcNowMilliseconds();
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM dungeon_persistent_effect_outbox
WHERE character_id = @characterId
  AND (state IN (@pending, @failed)
       OR (state = @reserved AND lease_expires_at <= @now));";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue(
                    "@pending",
                    (int)DungeonPersistentEffectState.Pending);
                command.Parameters.AddWithValue(
                    "@reserved",
                    (int)DungeonPersistentEffectState.Reserved);
                command.Parameters.AddWithValue(
                    "@failed",
                    (int)DungeonPersistentEffectState.Failed);
                command.Parameters.AddWithValue("@now", now);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private bool TryFinishReservation(
            DungeonPersistentEffectReservation reservation,
            DungeonPersistentEffectState state,
            string error)
        {
            if (!reservation.IsValid
                || (state != DungeonPersistentEffectState.Failed
                    && state != DungeonPersistentEffectState.DeadLetter))
            {
                return false;
            }

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE dungeon_persistent_effect_outbox
SET state = @state,
    lease_id = NULL,
    lease_owner = NULL,
    lease_expires_at = 0,
    last_error = @error,
    updated_at = @now
WHERE source_event_id = @eventId
  AND effect_kind = @effectKind
  AND effect_scope = @effectScope
  AND scope_target = @scopeTarget
  AND state = @reserved
  AND lease_id = @leaseId
  AND lease_owner = @leaseOwner;";
                AddIdentityParameters(command, reservation.EffectId);
                command.Parameters.AddWithValue("@state", (int)state);
                command.Parameters.AddWithValue(
                    "@reserved",
                    (int)DungeonPersistentEffectState.Reserved);
                command.Parameters.AddWithValue(
                    "@leaseId",
                    reservation.LeaseId.ToString("N"));
                command.Parameters.AddWithValue(
                    "@leaseOwner",
                    reservation.LeaseOwner);
                command.Parameters.AddWithValue(
                    "@error",
                    Truncate(error, MaximumErrorLength));
                command.Parameters.AddWithValue("@now", _utcNowMilliseconds());
                if (command.ExecuteNonQuery() != 1)
                    return false;
                transaction.Commit();
                return true;
            }
        }

        private void ReleaseForeignProcessReservations()
        {
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE dungeon_persistent_effect_outbox
SET state = @failed,
    lease_id = NULL,
    lease_owner = NULL,
    lease_expires_at = 0,
    last_error = 'reservation owner changed after server restart',
    updated_at = @now
WHERE state = @reserved
  AND lease_owner IS NOT NULL
  AND lease_owner <> @leaseOwner;";
                command.Parameters.AddWithValue(
                    "@failed",
                    (int)DungeonPersistentEffectState.Failed);
                command.Parameters.AddWithValue(
                    "@reserved",
                    (int)DungeonPersistentEffectState.Reserved);
                command.Parameters.AddWithValue("@leaseOwner", _leaseOwner);
                command.Parameters.AddWithValue("@now", _utcNowMilliseconds());
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        private static bool InsertPending(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DungeonPersistentEffectDefinition definition,
            long now)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO dungeon_persistent_effect_outbox (
    source_event_id, effect_kind, effect_scope, scope_target,
    character_id, account_id, payload_version, payload_json,
    state, created_at, updated_at)
VALUES (
    @eventId, @effectKind, @effectScope, @scopeTarget,
    @characterId, @accountId, @payloadVersion, @payloadJson,
    @pending, @now, @now);";
                AddIdentityParameters(command, definition.EffectId);
                command.Parameters.AddWithValue(
                    "@characterId",
                    definition.CharacterId);
                command.Parameters.AddWithValue("@accountId", definition.AccountId);
                command.Parameters.AddWithValue(
                    "@payloadVersion",
                    definition.PayloadVersion);
                command.Parameters.AddWithValue(
                    "@payloadJson",
                    definition.PayloadJson);
                command.Parameters.AddWithValue(
                    "@pending",
                    (int)DungeonPersistentEffectState.Pending);
                command.Parameters.AddWithValue("@now", now);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static DungeonPersistentEffectRecord Load(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DungeonEffectId effectId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT source_event_id, effect_kind, effect_scope, scope_target,
       character_id, account_id, payload_version, payload_json,
       state, lease_id, lease_owner, lease_expires_at, attempt_count,
       last_error, result_version, result_json,
       created_at, updated_at, committed_at
FROM dungeon_persistent_effect_outbox
WHERE source_event_id = @eventId
  AND effect_kind = @effectKind
  AND effect_scope = @effectScope
  AND scope_target = @scopeTarget;";
                AddIdentityParameters(command, effectId);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? ReadRecord(reader) : null;
            }
        }

        private static DungeonPersistentEffectRecord ReadRecord(
            SqliteDataReader reader)
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out var eventId))
                throw new InvalidOperationException(
                    "Persistent dungeon effect contains an invalid event ID.");
            var effectKind = reader.GetString(1);
            var effectScope = (DungeonEffectScope)reader.GetInt32(2);
            var scopeTarget = reader.GetInt64(3);
            return new DungeonPersistentEffectRecord
            {
                EffectId = new DungeonEffectId(
                    eventId,
                    effectKind,
                    effectScope,
                    scopeTarget),
                CharacterId = reader.GetInt32(4),
                AccountId = reader.GetInt32(5),
                PayloadVersion = reader.GetInt32(6),
                PayloadJson = reader.GetString(7),
                State = (DungeonPersistentEffectState)reader.GetInt32(8),
                LeaseId = ReadGuid(reader, 9),
                LeaseOwner = reader.IsDBNull(10) ? null : reader.GetString(10),
                LeaseExpiresAt = reader.GetInt64(11),
                AttemptCount = reader.GetInt32(12),
                LastError = reader.GetString(13),
                ResultVersion = reader.IsDBNull(14)
                    ? null
                    : reader.GetInt32(14),
                ResultJson = reader.IsDBNull(15) ? null : reader.GetString(15),
                CreatedAt = reader.GetInt64(16),
                UpdatedAt = reader.GetInt64(17),
                CommittedAt = reader.IsDBNull(18)
                    ? null
                    : reader.GetInt64(18),
            };
        }

        private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
                return Guid.Empty;
            return Guid.TryParseExact(reader.GetString(ordinal), "N", out var value)
                ? value
                : Guid.Empty;
        }

        private static void EnsureDefinitionMatches(
            DungeonPersistentEffectRecord stored,
            DungeonPersistentEffectDefinition requested)
        {
            if (stored.CharacterId != requested.CharacterId
                || stored.AccountId != requested.AccountId
                || stored.PayloadVersion != requested.PayloadVersion
                || !string.Equals(
                    stored.PayloadJson,
                    requested.PayloadJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Persistent dungeon effect ID was reused with a different payload.");
            }
        }

        private static void ValidateDefinition(
            DungeonPersistentEffectDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (definition.CharacterId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(definition.CharacterId));
            if (definition.AccountId < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(definition.AccountId));
            if (definition.PayloadVersion <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(definition.PayloadVersion));
            if (string.IsNullOrWhiteSpace(definition.PayloadJson))
                throw new ArgumentException(
                    "A persistent effect payload is required.",
                    nameof(definition.PayloadJson));
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static void AddIdentityParameters(
            SqliteCommand command,
            DungeonEffectId effectId)
        {
            command.Parameters.AddWithValue(
                "@eventId",
                effectId.SourceEventId.ToString("N"));
            command.Parameters.AddWithValue("@effectKind", effectId.EffectKind);
            command.Parameters.AddWithValue(
                "@effectScope",
                (int)effectId.Scope);
            command.Parameters.AddWithValue(
                "@scopeTarget",
                effectId.ScopeTarget);
        }

        private static long AddSaturating(long value, long add)
        {
            if (add > 0 && value > long.MaxValue - add)
                return long.MaxValue;
            if (add < 0 && value < long.MinValue - add)
                return long.MinValue;
            return value + add;
        }

        private static string Truncate(string value, int maximumLength)
        {
            value ??= string.Empty;
            return value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength);
        }
    }
}
