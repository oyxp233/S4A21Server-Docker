using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Raid
{
    public sealed class RaidLeaveResult
    {
        public bool Ok { get; init; }
        public bool Disbanded { get; init; }
        public uint RaidId { get; init; }
        public RaidSnapshot PreviousRaid { get; init; }
        public RaidSnapshot RemainingRaid { get; init; }
    }

    public sealed class RaidManager
    {
        private readonly object _lock = new object();
        private readonly Dictionary<uint, RaidAggregate> _raids = new Dictionary<uint, RaidAggregate>();
        private readonly Dictionary<ushort, uint> _userToRaid = new Dictionary<ushort, uint>();
        private readonly Dictionary<Guid, ushort> _sessionToUser = new Dictionary<Guid, ushort>();
        private readonly Dictionary<uint, List<RaidDungeonParticipation>> _dungeonParticipations =
            new Dictionary<uint, List<RaidDungeonParticipation>>();
        private readonly Dictionary<uint, Dictionary<uint, uint>> _dungeonClearCounts =
            new Dictionary<uint, Dictionary<uint, uint>>();
        private readonly Dictionary<uint, HashSet<ushort>> _clearParticipants =
            new Dictionary<uint, HashSet<ushort>>();
        private readonly Func<long> _clockMilliseconds;

        public RaidManager()
            : this(() => Environment.TickCount64)
        {
        }

        internal RaidManager(Func<long> clockMilliseconds)
        {
            _clockMilliseconds = clockMilliseconds ?? throw new ArgumentNullException(nameof(clockMilliseconds));
        }

        public RaidSnapshot Create(byte[] titleBytes, RaidMember leader)
        {
            if (leader == null)
                throw new ArgumentNullException(nameof(leader));

            lock (_lock)
            {
                LeaveLocked(leader.UserId);
                var raidId = AllocateRaidId(leader.CharacterId);
                var raid = new RaidAggregate(raidId, (byte[])(titleBytes ?? Array.Empty<byte>()).Clone(), leader.Clone());
                _raids.Add(raidId, raid);
                _userToRaid[leader.UserId] = raidId;
                _sessionToUser[leader.SessionId] = leader.UserId;
                return raid.Snapshot();
            }
        }

        public bool TryGetByUser(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (_userToRaid.TryGetValue(userId, out var raidId) && _raids.TryGetValue(raidId, out var aggregate))
                {
                    raid = aggregate.Snapshot();
                    return true;
                }
                raid = null;
                return false;
            }
        }

        public bool TryUpdateTitle(ushort userId, byte[] titleBytes, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate))
                {
                    raid = null;
                    return false;
                }
                aggregate.TitleBytes = (byte[])(titleBytes ?? Array.Empty<byte>()).Clone();
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryAssignParty(ushort actingUserId, ushort targetUserId, uint partyIndex, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (partyIndex > ushort.MaxValue
                    || !TryGetAggregate(actingUserId, out var aggregate))
                {
                    raid = null;
                    return false;
                }

                if (actingUserId != targetUserId && aggregate.LeaderUserId != actingUserId)
                {
                    raid = null;
                    return false;
                }

                var member = aggregate.GetMember(targetUserId);
                if (member == null)
                {
                    raid = null;
                    return false;
                }
                member.PartyIndex = (ushort)partyIndex;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryBeginStart(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate)
                    || aggregate.LeaderUserId != userId
                    || aggregate.State != 0
                    || aggregate.StartPending)
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = true;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryCompleteStart(uint raidId, ushort leaderUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.LeaderUserId != leaderUserId
                    || !aggregate.StartPending
                    || aggregate.State != 0)
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = false;
                aggregate.State = 2;
                aggregate.StateArgument = 0;
                aggregate.PhaseIndex = 0;
                aggregate.PhaseStartedAtMilliseconds = _clockMilliseconds();
                aggregate.PhaseClearTimeSeconds = 0;
                aggregate.PhaseTimeExtensionSeconds = 0;
                aggregate.PhaseDeathCount = 0;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryRecordDeath(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || aggregate.GetMember(userId) == null)
                {
                    raid = null;
                    return false;
                }

                if (aggregate.PhaseDeathCount < uint.MaxValue)
                    aggregate.PhaseDeathCount++;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryRecordCoinUse(ushort userId, uint dungeonId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (dungeonId == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    return false;
                }

                foreach (var participation in entries)
                {
                    if (participation.DungeonId != dungeonId
                        || !participation.MemberKeys.Contains(userId))
                    {
                        continue;
                    }

                    if (participation.UsedCoinCount < uint.MaxValue)
                        participation.UsedCoinCount++;
                    raid = aggregate.Snapshot();
                    return true;
                }

                return false;
            }
        }

        public bool TryGrantAdditionalCoinUses(ushort userId, uint additionalCount, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (additionalCount == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    return false;
                }

                foreach (var participation in entries)
                {
                    if (!participation.MemberKeys.Contains(userId))
                        continue;

                    var usedBalance = participation.UsedCoinCount > participation.GrantedCoinCount
                        ? participation.UsedCoinCount - participation.GrantedCoinCount
                        : 0u;
                    var appliedCount = Math.Min(additionalCount, usedBalance);
                    if (appliedCount == 0)
                        return false;

                    participation.GrantedCoinCount += appliedCount;
                    raid = aggregate.Snapshot();
                    return true;
                }

                return false;
            }
        }

        public bool TryCancelStart(uint raidId, ushort leaderUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.LeaderUserId != leaderUserId
                    || !aggregate.StartPending
                    || aggregate.State != 0)
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = false;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryEnterDungeon(
            ushort userId,
            uint dungeonId,
            out RaidSnapshot raid,
            out IReadOnlyList<uint> memberKeys)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                if (dungeonId == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2)
                {
                    return false;
                }

                if (!_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    entries = new List<RaidDungeonParticipation>();
                    _dungeonParticipations.Add(aggregate.RaidId, entries);
                }

                foreach (var entry in entries)
                {
                    if (entry.MemberKeys.Contains(userId))
                        return false;
                }

                var enteringMember = aggregate.GetMember(userId);
                if (enteringMember == null)
                    return false;

                List<uint> memberKeysForParty = null;
                foreach (var group in BuildSituationGroups(aggregate.Members))
                {
                    foreach (var memberKey in group.MemberKeys)
                    {
                        if (memberKey != userId)
                            continue;

                        memberKeysForParty = new List<uint>(group.MemberKeys);
                        break;
                    }

                    if (memberKeysForParty != null)
                        break;
                }
                if (memberKeysForParty == null || memberKeysForParty.Count == 0)
                    return false;

                var participation = new RaidDungeonParticipation
                {
                    DungeonId = dungeonId,
                    MemberKeys = memberKeysForParty,
                };
                entries.Add(participation);
                raid = aggregate.Snapshot();
                memberKeys = participation.MemberKeys;
                return true;
            }
        }
        internal static IReadOnlyList<RaidSituationGroup> BuildSituationGroups(
            IReadOnlyList<RaidMember> members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));

            var partyGroupIndexes = new Dictionary<ushort, int>();
            var partyIndexes = new List<ushort>();
            var memberKeysByGroup = new List<List<uint>>();
            foreach (var member in members)
            {
                if (member == null)
                    continue;

                if (member.PartyIndex == 0)
                {
                    partyIndexes.Add(0);
                    memberKeysByGroup.Add(new List<uint> { member.UserId });
                    continue;
                }

                if (!partyGroupIndexes.TryGetValue(member.PartyIndex, out var groupIndex))
                {
                    groupIndex = memberKeysByGroup.Count;
                    partyGroupIndexes.Add(member.PartyIndex, groupIndex);
                    partyIndexes.Add(member.PartyIndex);
                    memberKeysByGroup.Add(new List<uint>());
                }
                memberKeysByGroup[groupIndex].Add(member.UserId);
            }

            var groups = new List<RaidSituationGroup>(memberKeysByGroup.Count);
            for (var index = 0; index < memberKeysByGroup.Count; index++)
            {
                groups.Add(new RaidSituationGroup
                {
                    SituationIndex = checked((ushort)index),
                    PartyIndex = partyIndexes[index],
                    MemberKeys = memberKeysByGroup[index].ToArray(),
                });
            }
            return groups;
        }

        internal static int GetSituationPageCount(IReadOnlyList<RaidMember> members)
        {
            const int groupsPerPage = 5;
            var groupCount = BuildSituationGroups(members).Count;
            return Math.Max(1, (groupCount + groupsPerPage - 1) / groupsPerPage);
        }


        public bool TryGetSituationGroups(
            uint raidId,
            out IReadOnlyList<RaidSituationGroup> situationGroups)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate))
                {
                    situationGroups = Array.Empty<RaidSituationGroup>();
                    return false;
                }

                _dungeonParticipations.TryGetValue(raidId, out var participations);
                var groups = BuildSituationGroups(aggregate.Members);
                var result = new List<RaidSituationGroup>(groups.Count);
                foreach (var group in groups)
                {
                    RaidDungeonParticipation participation = null;
                    if (participations != null)
                    {
                        foreach (var candidate in participations)
                        {
                            if (!candidate.MemberKeys.Any(group.MemberKeys.Contains))
                                continue;
                            participation = candidate;
                            break;
                        }
                    }

                    result.Add(new RaidSituationGroup
                    {
                        SituationIndex = group.SituationIndex,
                        PartyIndex = group.PartyIndex,
                        MemberKeys = group.MemberKeys.ToArray(),
                        DungeonId = participation?.DungeonId ?? 0,
                        DungeonCleared = participation?.Cleared ?? false,
                        UsedCoinCount = participation?.UsedCoinCount ?? 0,
                        GrantedCoinCount = participation?.GrantedCoinCount ?? 0,
                    });
                }

                situationGroups = result;
                return true;
            }
        }
        public bool TryClearDungeon(
            ushort userId,
            uint dungeonId,
            uint maxClearCount,
            out RaidSnapshot raid,
            out IReadOnlyList<uint> memberKeys,
            out uint clearCount)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                clearCount = 0;
                if (dungeonId == 0
                    || maxClearCount == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    return false;
                }

                RaidDungeonParticipation participation = null;
                foreach (var entry in entries)
                {
                    if (entry.DungeonId == dungeonId && entry.MemberKeys.Contains(userId))
                    {
                        participation = entry;
                        break;
                    }
                }
                if (participation == null)
                    return false;
                // Keep the participation visible until the party leaves the dungeon.
                if (participation.Cleared)
                    return false;
                participation.Cleared = true;
                if (!_dungeonClearCounts.TryGetValue(aggregate.RaidId, out var counts))
                {
                    counts = new Dictionary<uint, uint>();
                    _dungeonClearCounts.Add(aggregate.RaidId, counts);
                }

                counts.TryGetValue(dungeonId, out var previous);
                clearCount = Math.Min(maxClearCount, previous + 1);
                counts[dungeonId] = clearCount;
                if (!_clearParticipants.TryGetValue(aggregate.RaidId, out var clearParticipants))
                {
                    clearParticipants = new HashSet<ushort>();
                    _clearParticipants.Add(aggregate.RaidId, clearParticipants);
                }
                foreach (var memberKey in participation.MemberKeys)
                    clearParticipants.Add(checked((ushort)memberKey));
                raid = aggregate.Snapshot();
                memberKeys = participation.MemberKeys;
                return true;
            }
        }

        public bool TryAbandonDungeon(
            ushort userId,
            uint dungeonId,
            out RaidSnapshot raid,
            out IReadOnlyList<uint> memberKeys)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                if (dungeonId == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                    return false;

                RaidDungeonParticipation participation = null;
                foreach (var entry in entries)
                {
                    if (entry.DungeonId == dungeonId && entry.MemberKeys.Contains(userId))
                    {
                        participation = entry;
                        break;
                    }
                }
                if (participation == null)
                    return false;

                entries.Remove(participation);
                raid = aggregate.Snapshot();
                memberKeys = participation.MemberKeys;
                return true;
            }
        }

        public RaidLeaveResult Leave(ushort userId)
        {
            lock (_lock)
            {
                return LeaveLocked(userId) ?? new RaidLeaveResult { Ok = false };
            }
        }

        public RaidLeaveResult OnSessionDisconnected(Guid sessionId)
        {
            lock (_lock)
            {
                if (!_sessionToUser.TryGetValue(sessionId, out var userId))
                    return new RaidLeaveResult { Ok = false };
                return LeaveLocked(userId) ?? new RaidLeaveResult { Ok = false };
            }
        }

        public bool TryGetByRaidId(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (_raids.TryGetValue(raidId, out var aggregate))
                {
                    raid = aggregate.Snapshot();
                    return true;
                }

                raid = null;
                return false;
            }
        }

        public bool TryGetClearCount(uint raidId, uint dungeonId, out uint clearCount)
        {
            lock (_lock)
            {
                clearCount = 0;
                return _dungeonClearCounts.TryGetValue(raidId, out var counts)
                    && counts.TryGetValue(dungeonId, out clearCount);
            }
        }

        public bool HasClearedDungeon(uint raidId, ushort userId)
        {
            lock (_lock)
            {
                return _clearParticipants.TryGetValue(raidId, out var participants)
                    && participants.Contains(userId);
            }
        }

        public bool TryExtendPhaseTime(
            uint raidId,
            uint baseDurationSeconds,
            uint additionalSeconds,
            out RaidSnapshot raid,
            out uint remainingSeconds)
        {
            lock (_lock)
            {
                remainingSeconds = 0;
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.State != 2
                    || aggregate.PhaseStartedAtMilliseconds < 0)
                {
                    raid = null;
                    return false;
                }

                var elapsedSeconds = (ulong)(Math.Max(
                    0L,
                    _clockMilliseconds() - aggregate.PhaseStartedAtMilliseconds) / 1000L);
				// The timer may be restored up to 40 minutes remaining. Cap the
				// resulting remaining time rather than the total scheduled duration;
				// otherwise a phase that starts at 40 minutes can never use this buff.
                const uint maxPhaseDurationSeconds = 2400u;
				var totalSeconds = (ulong)baseDurationSeconds + aggregate.PhaseTimeExtensionSeconds;
				var currentRemaining = totalSeconds > elapsedSeconds
					? totalSeconds - elapsedSeconds
					: 0UL;
				var availableRoom = currentRemaining >= maxPhaseDurationSeconds
					? 0UL
					: maxPhaseDurationSeconds - currentRemaining;
				var appliedExtension = Math.Min((ulong)additionalSeconds, availableRoom);
				if (appliedExtension == 0)
				{
					raid = null;
					return false;
				}
				aggregate.PhaseTimeExtensionSeconds = checked(
					aggregate.PhaseTimeExtensionSeconds + (uint)appliedExtension);
				remainingSeconds = checked((uint)(currentRemaining + appliedExtension));
                raid = aggregate.Snapshot();
                return true;
            }
        }
        public bool TryEnterPhaseBreak(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate) || aggregate.State != 2)
                {
                    raid = null;
                    return false;
                }

                var elapsedMilliseconds = aggregate.PhaseStartedAtMilliseconds >= 0
                    ? Math.Max(0, _clockMilliseconds() - aggregate.PhaseStartedAtMilliseconds)
                    : 0;
                aggregate.PhaseClearTimeSeconds = checked((uint)Math.Min(
                    (long)uint.MaxValue,
                    elapsedMilliseconds / 1000));
                aggregate.State = 3;
                aggregate.StateArgument = 0;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryFailPhase(uint raidId, uint phaseIndex, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.State != 2
                    || aggregate.PhaseIndex != phaseIndex)
                {
                    raid = null;
                    return false;
                }

                var elapsedMilliseconds = aggregate.PhaseStartedAtMilliseconds >= 0
                    ? Math.Max(0, _clockMilliseconds() - aggregate.PhaseStartedAtMilliseconds)
                    : 0;
                aggregate.PhaseClearTimeSeconds = checked((uint)Math.Min(
                    (long)uint.MaxValue,
                    elapsedMilliseconds / 1000));
                aggregate.StartPending = false;
                aggregate.State = 4;
                // Distinguish timeout failure from the successful final reward state.
                aggregate.StateArgument = 1;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryCompletePhase(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate) || aggregate.State != 3)
                {
                    raid = null;
                    return false;
                }

                // State 5 is the between-phase standby UI. The final phase stays
                // in reward state 4 after its rewards have completed.
                aggregate.State = aggregate.PhaseIndex == 0 ? 5u : 4u;
                aggregate.StateArgument = 0;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryPrepareNextPhase(ushort leaderUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(leaderUserId, out var aggregate)
                    || aggregate.LeaderUserId != leaderUserId)
                {
                    raid = null;
                    return false;
                }

                return TryPrepareNextPhaseLocked(aggregate, out raid);
            }
        }

        public bool TryPrepareNextPhaseAutomatically(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate))
                {
                    raid = null;
                    return false;
                }

                return TryPrepareNextPhaseLocked(aggregate, out raid);
            }
        }

        public bool TryCompletePreparedNextPhase(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.State != 5
                    || aggregate.StateArgument != 0
                    || aggregate.PhaseIndex != 0
                    || !aggregate.StartPending)
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = false;
                aggregate.State = 2;
                aggregate.StateArgument = 1;
                aggregate.PhaseIndex = 1;
                aggregate.PhaseStartedAtMilliseconds = _clockMilliseconds();
                aggregate.PhaseClearTimeSeconds = 0;
                aggregate.PhaseTimeExtensionSeconds = 0;
                aggregate.PhaseDeathCount = 0;
                _dungeonParticipations.Remove(aggregate.RaidId);
                _dungeonClearCounts.Remove(aggregate.RaidId);
                _clearParticipants.Remove(aggregate.RaidId);
                raid = aggregate.Snapshot();
                return true;
            }
        }
        public void ResetClearCounts(uint raidId, IEnumerable<uint> dungeonIds)
        {
            if (dungeonIds == null)
                return;

            lock (_lock)
            {
                if (!_dungeonClearCounts.TryGetValue(raidId, out var counts))
                    return;

                foreach (var dungeonId in dungeonIds)
                    counts.Remove(dungeonId);
            }
        }
        private RaidLeaveResult LeaveLocked(ushort userId)
        {
            if (!TryGetAggregate(userId, out var raid))
                return null;

            var previous = raid.Snapshot();
            var member = raid.GetMember(userId);
            raid.RemoveMember(userId);
            _userToRaid.Remove(userId);
            if (member != null)
                _sessionToUser.Remove(member.SessionId);

            if (raid.Members.Count == 0 || raid.LeaderUserId == userId)
            {
                foreach (var remaining in raid.Members)
                {
                    _userToRaid.Remove(remaining.UserId);
                    _sessionToUser.Remove(remaining.SessionId);
                }
                _raids.Remove(raid.RaidId);
                _dungeonParticipations.Remove(raid.RaidId);
                _dungeonClearCounts.Remove(raid.RaidId);
                _clearParticipants.Remove(raid.RaidId);
                return new RaidLeaveResult
                {
                    Ok = true,
                    Disbanded = true,
                    RaidId = raid.RaidId,
                    PreviousRaid = previous,
                };
            }

            return new RaidLeaveResult
            {
                Ok = true,
                RaidId = raid.RaidId,
                PreviousRaid = previous,
                RemainingRaid = raid.Snapshot(),
            };
        }

        private bool TryGetAggregate(ushort userId, out RaidAggregate raid)
        {
            if (_userToRaid.TryGetValue(userId, out var raidId) && _raids.TryGetValue(raidId, out raid))
                return true;
            raid = null;
            return false;
        }

        private static bool TryPrepareNextPhaseLocked(RaidAggregate aggregate, out RaidSnapshot raid)
        {
            if (aggregate.State != 5
                || aggregate.StateArgument != 0
                || aggregate.PhaseIndex != 0
                || aggregate.StartPending)
            {
                raid = null;
                return false;
            }

            aggregate.StartPending = true;
            raid = aggregate.Snapshot();
            return true;
        }
        private sealed class RaidDungeonParticipation
        {
            public uint DungeonId { get; init; }
            public List<uint> MemberKeys { get; init; } = new List<uint>();
            public bool Cleared { get; set; }
            public uint UsedCoinCount { get; set; }
            public uint GrantedCoinCount { get; set; }
        }

        private uint AllocateRaidId(uint preferred)
        {
            var candidate = preferred == 0 ? 1u : preferred;
            while (_raids.ContainsKey(candidate))
                candidate = candidate == uint.MaxValue ? 1u : candidate + 1u;
            return candidate;
        }
    }
}
