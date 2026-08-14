using DfoServer.Game.Session;
using System;

namespace DfoServer.Game.Dungeon
{
    internal sealed class LinkedDungeonEntryAuthorization
    {
        internal int CharacterId { get; set; }
        internal int SourceDungeonId { get; set; }
        internal int TargetDungeonId { get; set; }
        internal byte Difficulty { get; set; }
        internal DateTime ExpiresAtUtc { get; set; }
    }

    internal static class LinkedDungeonEntryAuthorizationStore
    {
        private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

        internal static void Grant(
            PlayerContext player,
            int sourceDungeonId,
            int targetDungeonId,
            byte difficulty)
            => Grant(
                player,
                sourceDungeonId,
                targetDungeonId,
                difficulty,
                DateTime.UtcNow,
                DefaultLifetime);

        internal static void Grant(
            PlayerContext player,
            int sourceDungeonId,
            int targetDungeonId,
            byte difficulty,
            DateTime utcNow,
            TimeSpan lifetime)
        {
            if (player == null
                || player.CharacterId <= 0
                || sourceDungeonId <= 0
                || targetDungeonId <= 0)
            {
                return;
            }

            var effectiveLifetime = lifetime > TimeSpan.Zero
                ? lifetime
                : DefaultLifetime;
            lock (player.LinkedDungeonEntryAuthorizationSyncRoot)
            {
                player.PendingLinkedDungeonEntryAuthorization =
                    new LinkedDungeonEntryAuthorization
                    {
                        CharacterId = player.CharacterId,
                        SourceDungeonId = sourceDungeonId,
                        TargetDungeonId = targetDungeonId,
                        Difficulty = difficulty,
                        ExpiresAtUtc = utcNow.Add(effectiveLifetime),
                    };
            }
        }

        internal static bool TryConsume(
            PlayerContext player,
            int targetDungeonId,
            byte difficulty,
            out int sourceDungeonId,
            out string reason)
            => TryConsume(
                player,
                targetDungeonId,
                difficulty,
                DateTime.UtcNow,
                out sourceDungeonId,
                out reason);

        internal static bool TryConsume(
            PlayerContext player,
            int targetDungeonId,
            byte difficulty,
            DateTime utcNow,
            out int sourceDungeonId,
            out string reason)
        {
            sourceDungeonId = 0;
            if (player == null)
            {
                reason = "no player";
                return false;
            }

            lock (player.LinkedDungeonEntryAuthorizationSyncRoot)
            {
                var authorization = player.PendingLinkedDungeonEntryAuthorization;
                if (authorization == null)
                {
                    reason = "no authorization";
                    return false;
                }

                // Every selection attempt consumes the pending transition. A wrong
                // target must not leave a reusable ticket behind.
                player.PendingLinkedDungeonEntryAuthorization = null;

                if (authorization.ExpiresAtUtc <= utcNow)
                {
                    reason = "authorization expired";
                    return false;
                }
                if (authorization.CharacterId != player.CharacterId)
                {
                    reason = "character changed";
                    return false;
                }
                if (authorization.TargetDungeonId != targetDungeonId)
                {
                    reason = "target mismatch";
                    return false;
                }
                if (authorization.Difficulty != difficulty)
                {
                    reason = "difficulty mismatch";
                    return false;
                }

                sourceDungeonId = authorization.SourceDungeonId;
                reason = "authorized";
                return sourceDungeonId > 0;
            }
        }

        internal static void Clear(PlayerContext player)
        {
            if (player == null)
                return;

            lock (player.LinkedDungeonEntryAuthorizationSyncRoot)
                player.PendingLinkedDungeonEntryAuthorization = null;
        }

        internal static bool HasPending(PlayerContext player)
        {
            if (player == null)
                return false;

            lock (player.LinkedDungeonEntryAuthorizationSyncRoot)
                return player.PendingLinkedDungeonEntryAuthorization != null;
        }
    }
}
