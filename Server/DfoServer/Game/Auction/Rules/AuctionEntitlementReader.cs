using Microsoft.Data.Sqlite;
using System;
using System.Data;
using System.IO;

namespace DfoServer.Game.Auction.Rules
{
    public sealed class AuctionEntitlementSnapshot
    {
        internal AuctionEntitlementSnapshot(int accountId, long? expiresAtUnix, long observedAtUnix)
        {
            AccountId = accountId;
            ExpiresAtUnix = expiresAtUnix;
            ObservedAtUnix = observedAtUnix;
        }
        public int AccountId { get; }
        public int PremiumType => AuctionEntitlementReader.AuctionPremiumType;
        public long? ExpiresAtUnix { get; }
        public long ObservedAtUnix { get; }
        public bool IsActive => ExpiresAtUnix.HasValue && ExpiresAtUnix.Value > ObservedAtUnix;
    }

    public static class AuctionEntitlementReader
    {
        // etc/premiumlist_new.etc [type]29 [target item]137, items193/901.
        public const int AuctionPremiumType = 29;

        // Caller supplies an open PUBLIC database connection and the operation's explicit time.
        // No implicit connection, wall clock, schema changes or premium cache.
        public static AuctionEntitlementSnapshot Read(SqliteConnection connection,
            SqliteTransaction transaction, int accountId, long nowUnix)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (connection.State != ConnectionState.Open) throw new InvalidOperationException("Public database connection must be open.");
            if (transaction != null && !ReferenceEquals(transaction.Connection, connection))
                throw new ArgumentException("Transaction belongs to a different connection.", nameof(transaction));
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            if (nowUnix < 0) throw new ArgumentOutOfRangeException(nameof(nowUnix));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT end_time FROM account_premiums WHERE account_id=@account AND premium_type=@type;";
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@type", AuctionPremiumType);
            var raw = command.ExecuteScalar();
            long? expiry = raw == null || raw == DBNull.Value ? null : Convert.ToInt64(raw);
            if (expiry.HasValue && expiry.Value < 0)
                throw new InvalidDataException("Invalid auction entitlement expiry.");
            return new AuctionEntitlementSnapshot(accountId, expiry, nowUnix);
        }
    }
}
