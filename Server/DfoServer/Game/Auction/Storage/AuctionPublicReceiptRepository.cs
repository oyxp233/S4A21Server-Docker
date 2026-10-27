using System;
using System.Text.Json;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction.Storage
{
    internal sealed class AuctionReceiptConflictException : InvalidOperationException
    {
        internal AuctionReceiptConflictException(string message) : base(message) { }
    }

    internal sealed record AuctionPublicReceipt(int FormatVersion, Guid OperationId,
        string IntentHash, int CharacterId, int AccountId, string ResultJson);

    // The caller's public asset transaction must include this receipt and all
    // inventory/mail effects. The audit table has no unique action constraint;
    // use the market gate and an IMMEDIATE transaction, checking Find first.
    internal static class AuctionPublicReceiptRepository
    {
        internal static AuctionPublicReceipt Find(SqliteConnection c, SqliteTransaction t,
            AuctionOperationIntent intent)
        {
            if (c == null || intent == null) throw new ArgumentNullException();
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = @"SELECT character_id,account_id,payload_json FROM inventory_audit_log
WHERE action_name=@action ORDER BY audit_id LIMIT 2;";
            cmd.Parameters.AddWithValue("@action", ActionName(intent.OperationId));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null; // Only a successful read can prove absence.
            var cid = reader.GetInt32(0); var aid = reader.GetInt32(1);
            var receipt = JsonSerializer.Deserialize<AuctionPublicReceipt>(reader.GetString(2))
                ?? throw new AuctionReceiptConflictException("Missing auction public receipt payload.");
            if (reader.Read()) throw new AuctionReceiptConflictException("Duplicate auction public receipts.");
            if (receipt.FormatVersion != 1 || receipt.OperationId != intent.OperationId
                || receipt.IntentHash != intent.IntentHash || receipt.CharacterId != intent.CharacterId
                || receipt.AccountId != intent.AccountId || cid != intent.CharacterId || aid != intent.AccountId)
                throw new AuctionReceiptConflictException("Auction public receipt identity conflict.");
            try { AuctionOperationIntent.RequireObject(receipt.ResultJson); }
            catch (ArgumentException) { throw new AuctionReceiptConflictException("Invalid auction receipt result."); }
            return receipt;
        }

        internal static AuctionPublicReceipt Insert(SqliteConnection c, SqliteTransaction t,
            AuctionOperationIntent intent, Guid sessionId, string resultJson)
        {
            if (c == null || t == null || !ReferenceEquals(t.Connection, c))
                throw new ArgumentException("Auction receipt requires the matching asset transaction.");
            AuctionOperationIntent.RequireObject(resultJson);
            var existing = Find(c, t, intent);
            if (existing != null)
            {
                if (existing.ResultJson != resultJson) throw new InvalidOperationException("Auction receipt result conflict.");
                return existing;
            }
            var receipt = new AuctionPublicReceipt(1, intent.OperationId, intent.IntentHash,
                intent.CharacterId, intent.AccountId, resultJson);
            InventoryAuditRepository.Insert(c, t, new InventoryAuditEvent {
                SessionId = sessionId, OwnerId = intent.CharacterId, CharacterId = intent.CharacterId,
                AccountId = intent.AccountId, ActionName = ActionName(intent.OperationId),
                PayloadJson = JsonSerializer.Serialize(receipt) });
            return receipt;
        }

        private static string ActionName(Guid operationId) => "auction_asset:" + operationId.ToString("N");
    }
}
