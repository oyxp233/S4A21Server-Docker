using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DfoServer.Game.Auction.Storage
{
    // Domain actions are deliberately separate from wire opcode/selector values.
    internal enum AuctionOperationAction { List = 1, Bid, Buyout, BuyPiece, Cancel, Expire }
    internal enum AuctionOperationState { Prepared, Committed, Aborted, Quarantined }

    internal sealed record AuctionOperationIntent
    {
        internal Guid OperationId { get; }
        internal string ResourceKey { get; }
        internal AuctionOperationAction Action { get; }
        internal int CharacterId { get; }
        internal int AccountId { get; }
        internal long ExpectedVersion { get; }
        internal long CreatedAtUnix { get; }
        internal string IntentJson { get; }
        internal string IntentHash { get; }

        internal AuctionOperationIntent(Guid operationId, string resourceKey,
            AuctionOperationAction action, int characterId, int accountId,
            long expectedVersion, long createdAtUnix, string intentJson)
        {
            if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(resourceKey)
                || resourceKey.Length > 160 || !Enum.IsDefined(action)
                || characterId <= 0 || accountId <= 0 || expectedVersion < 0 || createdAtUnix <= 0)
                throw new ArgumentException("Invalid auction operation identity.");
            RequireObject(intentJson);
            OperationId = operationId; ResourceKey = resourceKey; Action = action;
            CharacterId = characterId; AccountId = accountId; ExpectedVersion = expectedVersion;
            CreatedAtUnix = createdAtUnix; IntentJson = intentJson;
            // Hash the exact frozen JSON as a string. Do not recanonicalize or
            // resample time during retries: every field below is immutable.
            var identity = JsonSerializer.Serialize(new object[] {
                1, operationId.ToString("N"), resourceKey, (int)action, characterId,
                accountId, expectedVersion, createdAtUnix, intentJson });
            IntentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        internal static void RequireObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Missing frozen JSON.");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Auction payload must be a JSON object.");
        }

        internal void RequireSame(AuctionOperationIntent other)
        {
            if (other == null || OperationId != other.OperationId || IntentHash != other.IntentHash)
                throw new InvalidOperationException("Auction operation identity conflict.");
        }
    }

    internal sealed record AuctionJournalEntry(long SequenceId, AuctionOperationIntent Intent,
        AuctionOperationState State, string ResultJson, string ResolutionReason);
}
