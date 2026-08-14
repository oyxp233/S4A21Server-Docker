using System;

namespace DfoServer.Game.Raid
{
    // Contract-only seam for future raid content. It intentionally owns no
    // channel/town routing, event schedule, party attack flow, timer, reward,
    // persistence, or protocol registration. Normal Anton never depends on it.
    internal enum RaidContentAvailability : byte
    {
        NotConfigured = 0,
        Available = 1,
    }

    internal enum RaidContentAccessStatus : byte
    {
        NotApplicable = 0,
        NotConfigured = 1,
        Allowed = 2,
        Denied = 3,
    }

    internal readonly struct RaidContentRequest
    {
        internal RaidContentRequest(
            string contentKey,
            int characterId,
            int accountId,
            int requestedDungeonId)
        {
            ContentKey = contentKey ?? string.Empty;
            CharacterId = characterId;
            AccountId = accountId;
            RequestedDungeonId = requestedDungeonId;
        }

        internal string ContentKey { get; }
        internal int CharacterId { get; }
        internal int AccountId { get; }
        internal int RequestedDungeonId { get; }
    }

    internal readonly struct RaidContentAccessDecision
    {
        internal RaidContentAccessDecision(
            RaidContentAccessStatus status,
            string reason)
        {
            Status = status;
            Reason = reason ?? string.Empty;
        }

        internal RaidContentAccessStatus Status { get; }
        internal string Reason { get; }
        internal bool IsAllowed => Status == RaidContentAccessStatus.Allowed;

        internal static RaidContentAccessDecision NotConfigured()
            => new RaidContentAccessDecision(
                RaidContentAccessStatus.NotConfigured,
                "raid content gateway is not configured");
    }

    // A later raid implementation can replace this gateway in its own
    // composition root. This project keeps the default disabled and does not
    // invoke it from normal dungeon selection.
    internal interface IRaidContentGateway
    {
        RaidContentAvailability Availability { get; }

        RaidContentAccessDecision EvaluateEntry(RaidContentRequest request);
    }

    internal sealed class DisabledRaidContentGateway : IRaidContentGateway
    {
        internal static DisabledRaidContentGateway Instance { get; } =
            new DisabledRaidContentGateway();

        private DisabledRaidContentGateway()
        {
        }

        public RaidContentAvailability Availability =>
            RaidContentAvailability.NotConfigured;

        public RaidContentAccessDecision EvaluateEntry(
            RaidContentRequest request)
            => RaidContentAccessDecision.NotConfigured();
    }
}
