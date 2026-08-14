using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryContext
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<Guid, int> SessionOwnership = new Dictionary<Guid, int>();
        private static readonly Dictionary<int, InventoryLease> CharacterLeases = new Dictionary<int, InventoryLease>();
        private static long _nextVersion;

        public static InventoryLease Register(Guid sessionId, InventoryService inventory)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            return Register(sessionId, inventory.CharacterId, inventory);
        }

        public static InventoryLease Register(Guid sessionId, int characterId, InventoryService inventory)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("注册在线背包需要有效的 sessionId。", nameof(sessionId));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId), "注册在线背包需要有效的角色 ID。");
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            if (inventory.CharacterId > 0 && inventory.CharacterId != characterId)
                throw new ArgumentException("注册在线背包时，参数角色 ID 与背包角色 ID 不一致。", nameof(characterId));

            InventoryLease existingForSession;
            InventoryLease existingForCharacter;
            InventoryLease existingForSameSession;
            lock (SyncRoot)
            {
                SessionOwnership.TryGetValue(sessionId, out var oldCharacterId);
                existingForSession = oldCharacterId > 0
                    && oldCharacterId != characterId
                    && CharacterLeases.TryGetValue(oldCharacterId, out var oldSessionLease)
                    ? oldSessionLease
                    : null;
                existingForCharacter = CharacterLeases.TryGetValue(
                    characterId,
                    out var oldCharacterLease)
                    && !oldCharacterLease.IsOwnedBy(sessionId)
                    ? oldCharacterLease
                    : null;
                existingForSameSession = CharacterLeases.TryGetValue(
                    characterId,
                    out var sameSessionLease)
                    && sameSessionLease.IsOwnedBy(sessionId)
                    ? sameSessionLease
                    : null;
            }

            if (!TryPersistBeforeReplacement(existingForSession, "register-session-replacement")
                || !TryPersistBeforeReplacement(existingForCharacter, "register-character-replacement")
                || !TryPersistBeforeReplacement(existingForSameSession, "register-same-session-replacement"))
            {
                throw new InvalidOperationException(
                    $"cannot replace inventory lease for character {characterId}: "
                    + "previous lease persistence failed");
            }

            InventoryLease lease;
            lock (SyncRoot)
            {
                if (!MatchesSessionReplacement(
                        existingForSession,
                        existingForSameSession,
                        sessionId,
                        characterId)
                    || !MatchesCharacterReplacement(
                        existingForCharacter,
                        existingForSameSession,
                        sessionId,
                        characterId))
                {
                    throw new InvalidOperationException(
                        $"inventory lease changed during replacement for character {characterId}");
                }

                RemovePreviousCharacterForSession(
                    sessionId,
                    characterId,
                    existingForSession);
                RemovePreviousSessionForCharacter(
                    sessionId,
                    characterId,
                    existingForCharacter);

                lease = new InventoryLease(sessionId, characterId, inventory, ++_nextVersion);
                SessionOwnership[sessionId] = characterId;
                CharacterLeases[characterId] = lease;
            }

            return lease;
        }

        public static bool Unregister(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
                return false;

            InventoryLease lease;
            lock (SyncRoot)
            {
                if (!SessionOwnership.TryGetValue(sessionId, out var characterId))
                    return false;
                if (!CharacterLeases.TryGetValue(characterId, out lease)
                    || !lease.IsOwnedBy(sessionId))
                    return false;
            }

            lock (lease.SyncRoot)
            {
                if (!TryPersistBeforeReplacement(lease, "unregister-session"))
                    return false;

                lock (SyncRoot)
                {
                    if (!IsCurrentLeaseReference(lease, sessionId, lease.CharacterId))
                        return false;

                    SessionOwnership.Remove(sessionId);
                    CharacterLeases.Remove(lease.CharacterId);
                    return true;
                }
            }
        }

        public static bool Unregister(Guid sessionId, int characterId)
        {
            if (sessionId == Guid.Empty || characterId <= 0)
                return false;

            InventoryLease lease;
            lock (SyncRoot)
            {
                if (!SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId)
                    || ownedCharacterId != characterId
                    || !CharacterLeases.TryGetValue(characterId, out lease)
                    || !lease.IsOwnedBy(sessionId))
                    return false;
            }

            lock (lease.SyncRoot)
            {
                if (!TryPersistBeforeReplacement(lease, "unregister-character"))
                    return false;

                lock (SyncRoot)
                {
                    if (!IsCurrentLeaseReference(lease, sessionId, characterId))
                        return false;

                    SessionOwnership.Remove(sessionId);
                    CharacterLeases.Remove(characterId);
                    return true;
                }
            }
        }

        public static InventoryService Get(int characterId)
        {
            return TryGet(characterId, out var inventory) ? inventory : null;
        }

        public static bool TryGet(int characterId, out InventoryService inventory)
        {
            inventory = null;
            if (characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                if (!CharacterLeases.TryGetValue(characterId, out var lease))
                    return false;

                inventory = lease.Inventory;
                return true;
            }
        }

        public static bool TryGetLease(int characterId, out InventoryLease lease)
        {
            lease = null;
            if (characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                return CharacterLeases.TryGetValue(characterId, out lease);
            }
        }

        internal static bool TryReplaceCurrentLease(
            InventoryLease expected,
            InventoryService replacement,
            out InventoryLease lease)
        {
            lease = null;
            if (expected == null || replacement == null)
            {
                return false;
            }

            lock (expected.SyncRoot)
            lock (SyncRoot)
            {
                if (replacement.CharacterId != expected.CharacterId
                    || replacement.AccountId != expected.AccountId
                    || !CharacterLeases.TryGetValue(expected.CharacterId, out var current)
                    || !ReferenceEquals(current, expected)
                    || !SessionOwnership.TryGetValue(expected.SessionId, out var characterId)
                    || characterId != expected.CharacterId)
                {
                    return false;
                }

                expected.ReplaceInventory(replacement);
                lease = expected;
                return true;
            }
        }

        public static bool TryGetOwnedLease(
            Guid sessionId,
            int characterId,
            out InventoryLease lease)
        {
            lease = null;
            if (sessionId == Guid.Empty || characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                if (!SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId)
                    || ownedCharacterId != characterId
                    || !CharacterLeases.TryGetValue(characterId, out var current)
                    || !current.IsOwnedBy(sessionId))
                {
                    return false;
                }

                lease = current;
                return true;
            }
        }

        public static bool IsCurrentLease(
            InventoryLease lease,
            Guid sessionId,
            int characterId)
        {
            if (lease == null
                || sessionId == Guid.Empty
                || characterId <= 0
                || lease.CharacterId != characterId
                || !lease.IsOwnedBy(sessionId))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId)
                    && ownedCharacterId == characterId
                    && CharacterLeases.TryGetValue(characterId, out var current)
                    && ReferenceEquals(current, lease)
                    && current.Version == lease.Version;
            }
        }

        public static IReadOnlyList<InventoryLease> GetLeasesSnapshot()
        {
            lock (SyncRoot)
                return new List<InventoryLease>(CharacterLeases.Values);
        }

        public static void SaveAllDirty()
        {
            InventoryPersistenceService.SaveAllDirty();
        }

        public static bool TryGetForSession(Guid sessionId, int characterId, out InventoryService inventory)
        {
            inventory = null;
            if (sessionId == Guid.Empty || characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                if (!SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId)
                    || ownedCharacterId != characterId)
                    return false;
                if (!CharacterLeases.TryGetValue(characterId, out var lease)
                    || !lease.IsOwnedBy(sessionId))
                    return false;

                inventory = lease.Inventory;
                return true;
            }
        }

        private static InventoryLease RemovePreviousCharacterForSession(
            Guid sessionId,
            int characterId,
            InventoryLease expected)
        {
            if (!SessionOwnership.TryGetValue(sessionId, out var oldCharacterId)
                || oldCharacterId == characterId
                || !CharacterLeases.TryGetValue(oldCharacterId, out var oldLease)
                || !ReferenceEquals(oldLease, expected))
                return null;

            SessionOwnership.Remove(sessionId);
            CharacterLeases.Remove(oldCharacterId);
            return oldLease;
        }

        private static InventoryLease RemovePreviousSessionForCharacter(
            Guid sessionId,
            int characterId,
            InventoryLease expected)
        {
            if (!CharacterLeases.TryGetValue(characterId, out var oldLease)
                || oldLease.IsOwnedBy(sessionId)
                || !ReferenceEquals(oldLease, expected))
                return null;

            if (SessionOwnership.TryGetValue(oldLease.SessionId, out var oldOwnedCharacterId)
                && oldOwnedCharacterId == characterId)
                SessionOwnership.Remove(oldLease.SessionId);

            CharacterLeases.Remove(characterId);
            return oldLease;
        }

        private static bool MatchesSessionReplacement(
            InventoryLease expected,
            InventoryLease sameSession,
            Guid sessionId,
            int characterId)
        {
            if (!SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId))
                return expected == null && sameSession == null;

            if (ownedCharacterId == characterId)
                return sameSession != null
                    && CharacterLeases.TryGetValue(characterId, out var sameCurrent)
                    && ReferenceEquals(sameCurrent, sameSession);

            return expected != null
                && ownedCharacterId == expected.CharacterId
                && CharacterLeases.TryGetValue(expected.CharacterId, out var current)
                && ReferenceEquals(current, expected);
        }

        private static bool MatchesCharacterReplacement(
            InventoryLease expected,
            InventoryLease sameSession,
            Guid sessionId,
            int characterId)
        {
            if (!CharacterLeases.TryGetValue(characterId, out var current))
                return expected == null && sameSession == null;

            if (sameSession != null)
                return ReferenceEquals(current, sameSession)
                    && current.IsOwnedBy(sessionId)
                    && expected == null;

            return expected != null && ReferenceEquals(current, expected);
        }

        private static bool IsCurrentLeaseReference(
            InventoryLease expected,
            Guid sessionId,
            int characterId)
        {
            return SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId)
                && ownedCharacterId == characterId
                && CharacterLeases.TryGetValue(characterId, out var current)
                && ReferenceEquals(current, expected);
        }

        private static bool TryPersistBeforeReplacement(
            InventoryLease lease,
            string operation)
        {
            if (lease == null)
                return true;

            var saved = InventoryPersistenceService.SaveDirty(lease);
            if (!saved)
            {
                FileLogger.Log(
                    $"[InventoryContext] lease persistence failed "
                    + $"operation={operation ?? "unknown"} "
                    + $"cid={lease.CharacterId} session={lease.SessionId}; "
                    + "lease retained for retry");
            }

            return saved;
        }
    }
}
