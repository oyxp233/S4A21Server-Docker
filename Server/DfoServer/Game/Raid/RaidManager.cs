using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Party;

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
        private long _nextPreparationGeneration;

        private readonly Dictionary<uint, RaidAggregate> _raids = new Dictionary<uint, RaidAggregate>();
        private readonly Dictionary<ushort, uint> _userToRaid = new Dictionary<ushort, uint>();
        private readonly Dictionary<Guid, ushort> _sessionToUser = new Dictionary<Guid, ushort>();
        private readonly Dictionary<uint, List<RaidDungeonParticipation>> _dungeonParticipations =
            new Dictionary<uint, List<RaidDungeonParticipation>>();
        private readonly Dictionary<uint, Dictionary<uint, uint>> _dungeonClearCounts =
            new Dictionary<uint, Dictionary<uint, uint>>();
        private readonly Dictionary<uint, HashSet<ushort>> _clearParticipants =
            new Dictionary<uint, HashSet<ushort>>();
        private readonly Dictionary<int, List<RaidMember>> _channelWaiting = new Dictionary<int, List<RaidMember>>();

        private readonly Func<long> _clockMilliseconds;

        public RaidManager()
            : this(() => Environment.TickCount64)
        {
        }

        internal RaidManager(Func<long> clockMilliseconds)
        {
            _clockMilliseconds = clockMilliseconds ?? throw new ArgumentNullException(nameof(clockMilliseconds));
        }

        internal bool TryGetCompletedTownReturn(ushort userId, Guid sessionId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (!TryGetAggregate(userId, out var raid2) || raid2.State != 5 || raid2.PhaseIndex != 0 || raid2.StartPending || !raid2.Members.Any((RaidMember m) => m.UserId == userId && m.SessionId == sessionId) || !_clearParticipants.TryGetValue(raid2.RaidId, out var value) || !value.Contains(userId))
                {
                    return false;
                }
                raid = raid2.Snapshot();
                return true;
            }
        }

        public RaidSnapshot Create(byte[] titleBytes, RaidMember leader, int channelId)
        {
            if (leader == null)
            {
                throw new ArgumentNullException("leader");
            }
            lock (_lock)
            {
                LeaveLocked(leader.UserId);
                uint num = AllocateRaidId((uint)(channelId << 16) | leader.CharacterId);
                RaidAggregate raidAggregate = new RaidAggregate(num, (byte[])(titleBytes ?? Array.Empty<byte>()).Clone(), leader.Clone());
                _raids.Add(num, raidAggregate);
                _userToRaid[leader.UserId] = num;
                _sessionToUser[leader.SessionId] = leader.UserId;
                return raidAggregate.Snapshot();
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

        internal bool TryCommitPartySettings(ushort userId, Guid sessionId, Action<IReadOnlyList<RaidMember>> commit)
        {
            lock (_lock)
            {
                IReadOnlyList<RaidMember> obj = null;
                if (TryGetAggregate(userId, out var raid))
                {
                    if (!raid.Members.Any((RaidMember m) => m.UserId == userId && m.SessionId == sessionId))
                    {
                        return false;
                    }
                    if (raid.StartPending || raid.State == 2 || raid.State == 5)
                    {
                        obj = raid.Snapshot().Members;
                    }
                }
                commit(obj);
                return true;
            }
        }

        internal bool TryCommitPartyJoin(PartyMember inviter, PartyMember invitee, Action<IReadOnlyList<RaidMember>> commit)
        {
            if (inviter == null || invitee == null || commit == null)
                return false;
            lock (_lock)
            {
                TryGetAggregate(inviter.UserId, out var inviterRaid);
                TryGetAggregate(invitee.UserId, out var inviteeRaid);
                if (inviterRaid == null && inviteeRaid == null)
                {
                    commit(null);
                    return true;
                }
                // A normal party invitation cannot bypass raid admission or a frozen preparation.
                if (inviterRaid == null || inviterRaid != inviteeRaid || inviterRaid.StartPending
                    || !inviterRaid.Members.Any(m => m.UserId == inviter.UserId && m.CharacterId == inviter.CharacterId && m.SessionId == inviter.SessionId)
                    || !inviterRaid.Members.Any(m => m.UserId == invitee.UserId && m.CharacterId == invitee.CharacterId && m.SessionId == invitee.SessionId))
                    return false;
                commit(inviterRaid.Snapshot().Members);
                return true;
            }
        }

        public bool TryFindRecruitingRaid(int channelId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                foreach (RaidAggregate value in _raids.Values)
                {
                    if (value.State == 0 && value.RaidId >> 16 == (uint)channelId)
                    {
                        raid = value.Snapshot();
                        return true;
                    }
                }
                raid = null;
                return false;
            }
        }

        public bool TryGetByMemberCharacterId(uint characterId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                foreach (RaidAggregate value in _raids.Values)
                {
                    if (value.Members.Any((RaidMember member) => member.CharacterId == characterId))
                    {
                        raid = value.Snapshot();
                        return true;
                    }
                }
                raid = null;
                return false;
            }
        }

        public IReadOnlyList<RaidSnapshot> ListRaidsByChannel(int channelId)
        {
            lock (_lock)
            {
                List<RaidSnapshot> list = new List<RaidSnapshot>();
                foreach (RaidAggregate value in _raids.Values)
                {
                    if (value.RaidId >> 16 == (uint)channelId)
                    {
                        list.Add(value.Snapshot());
                    }
                }
                return list;
            }
        }

        public bool TryAddWaiting(int channelId, RaidMember member)
        {
            if (member == null)
            {
                throw new ArgumentNullException("member");
            }
            lock (_lock)
            {
                RemoveWaitingLocked(member.UserId);
                if (!_channelWaiting.TryGetValue(channelId, out var value))
                {
                    value = new List<RaidMember>();
                    _channelWaiting.Add(channelId, value);
                }
                value.Add(member.Clone());
                return true;
            }
        }

        public bool TryRemoveWaiting(ushort userId)
        {
            lock (_lock)
            {
                return RemoveWaitingLocked(userId);
            }
        }

        private bool RemoveWaitingLocked(ushort userId)
        {
            bool result = false;
            foreach (List<RaidMember> value in _channelWaiting.Values)
            {
                for (int num = value.Count - 1; num >= 0; num--)
                {
                    if (value[num].UserId == userId)
                    {
                        value.RemoveAt(num);
                        result = true;
                    }
                }
            }
            return result;
        }

        public IReadOnlyList<RaidMember> GetWaitingList(int channelId)
        {
            lock (_lock)
            {
                if (!_channelWaiting.TryGetValue(channelId, out var value) || value.Count == 0)
                {
                    return Array.Empty<RaidMember>();
                }
                List<RaidMember> list = new List<RaidMember>(value.Count);
                foreach (RaidMember item in value)
                {
                    list.Add(item.Clone());
                }
                return list;
            }
        }

        public bool TryAddMember(uint raidId, RaidMember member, out RaidSnapshot raid)
        {
            if (member == null)
            {
                throw new ArgumentNullException("member");
            }
            lock (_lock)
            {
                raid = null;
                if (!_raids.TryGetValue(raidId, out var value) || value.State != 0 || value.GetMember(member.UserId) != null)
                {
                    return false;
                }
                LeaveLocked(member.UserId);
                if (!value.AddMember(member.Clone()))
                {
                    return false;
                }
                _userToRaid[member.UserId] = raidId;
                _sessionToUser[member.SessionId] = member.UserId;
                raid = value.Snapshot();
                return true;
            }
        }

        internal bool TryGetJoinableRaid(ushort leaderUserId, Guid leaderSessionId, ushort joinerUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (TryGetAggregate(leaderUserId, out var raid2) && raid2.LeaderUserId == leaderUserId)
                {
                    RaidMember member = raid2.GetMember(leaderUserId);
                    if (member != null && !(member.SessionId != leaderSessionId) && raid2.State == 0 && !raid2.StartPending && raid2.Members.Count < 20 && !_userToRaid.ContainsKey(joinerUserId))
                    {
                        raid = raid2.Snapshot();
                        return true;
                    }
                }
                return false;
            }
        }

        internal bool TryAddConfirmedMember(RaidSnapshot expected, RaidMember member, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (expected?.Leader != null && member != null && _raids.TryGetValue(expected.RaidId, out var value) && !(value.InstanceId != expected.InstanceId) && value.LeaderUserId == expected.LeaderUserId && !(value.LeadershipVersion != expected.LeadershipVersion))
                {
                    Guid? guid = value.GetMember(expected.LeaderUserId)?.SessionId;
                    Guid sessionId = expected.Leader.SessionId;
                    if (guid.HasValue && !(guid.GetValueOrDefault() != sessionId) && value.State == 0 && !value.StartPending && !_userToRaid.ContainsKey(member.UserId))
                    {
                        if (!value.AddMember(member.Clone()))
                        {
                            return false;
                        }
                        _userToRaid[member.UserId] = value.RaidId;
                        _sessionToUser[member.SessionId] = member.UserId;
                        raid = value.Snapshot();
                        return true;
                    }
                }
                return false;
            }
        }

        public bool RebindSession(ushort userId, Guid sessionId)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var raid))
                {
                    return false;
                }
                RaidMember member = raid.GetMember(userId);
                if (member == null)
                {
                    return false;
                }
                _sessionToUser.Remove(member.SessionId);
                member.SessionId = sessionId;
                _sessionToUser[sessionId] = userId;
                return true;
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

        internal bool TryTransferLeadership(ushort actingUserId, Guid actingSessionId, ushort targetUserId, Guid targetSessionId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (targetUserId != 0 && targetUserId != actingUserId && TryGetAggregate(actingUserId, out var raid2) && raid2.LeaderUserId == actingUserId && !raid2.StartPending)
                {
                    RaidMember member = raid2.GetMember(actingUserId);
                    if (member != null && !(member.SessionId != actingSessionId))
                    {
                        RaidMember member2 = raid2.GetMember(targetUserId);
                        if (member2 != null && !(member2.SessionId != targetSessionId))
                        {
                            raid2.LeaderUserId = targetUserId;
                            raid2.LeadershipVersion = Guid.NewGuid();
                            raid2.AssignmentVersion = Guid.NewGuid();
                            raid = raid2.Snapshot();
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        public bool TryAssignParty(ushort actingUserId, ushort targetUserId, uint partyIndex, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (partyIndex > 65535 || !TryGetAggregate(actingUserId, out var raid2))
                {
                    raid = null;
                    return false;
                }
                if (actingUserId != targetUserId && raid2.LeaderUserId != actingUserId)
                {
                    raid = null;
                    return false;
                }
                RaidMember member = raid2.GetMember(targetUserId);
                if (member == null)
                {
                    raid = null;
                    return false;
                }
                if (member.PartyIndex != partyIndex)
                {
                    raid2.AssignmentVersion = Guid.NewGuid();
                }
                member.PartyIndex = (ushort)partyIndex;
                raid = raid2.Snapshot();
                return true;
            }
        }

        internal PartyOpResult LeaveNormalParty(ushort userId, Guid sessionId, Func<PartyOpResult> leave, out RaidSnapshot updatedRaid)
        {
            lock (_lock)
            {
                updatedRaid = null;
                TryGetAggregate(userId, out var raid);
                RaidMember raidMember = raid?.GetMember(userId);
                if (raid != null && (raidMember == null || raidMember.SessionId != sessionId))
                {
                    return PartyOpResult.Fail("stale_raid_session");
                }
                PartyOpResult partyOpResult = leave();
                if (partyOpResult.Ok && raidMember != null && raidMember.PartyIndex != 0)
                {
                    raidMember.PartyIndex = 0;
                    raid.AssignmentVersion = Guid.NewGuid();
                    updatedRaid = raid.Snapshot();
                }
                return partyOpResult;
            }
        }

        internal bool TrySynchronizeJoinedParty(ushort userId, Guid sessionId, Func<DfoServer.Game.Party.Party> readCurrentParty, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (readCurrentParty != null && TryGetAggregate(userId, out var aggregate))
                {
                    RaidMember member = aggregate.GetMember(userId);
                    if (member != null && !(member.SessionId != sessionId) && !aggregate.StartPending && (aggregate.State == 2 || aggregate.State == 5))
                    {
                        DfoServer.Game.Party.Party party = readCurrentParty();
                        if (party != null && party.Count >= 1 && party.Count <= 4)
                        {
                            PartyMember member2 = party.GetMember(userId);
                            if (member2 != null && !(member2.SessionId != sessionId) && !party.Members.Any((PartyMember p) => !aggregate.Members.Any((RaidMember m) => m.UserId == p.UserId && m.CharacterId == p.CharacterId && m.SessionId == p.SessionId)))
                            {
                                if (party.Count == 1 && member.PartyIndex == 0)
                                {
                                    return false;
                                }
                                HashSet<ushort> ids = party.Members.Select((PartyMember p) => p.UserId).ToHashSet();
                                HashSet<ushort> occupied = (from m in aggregate.Members
                                    where !ids.Contains(m.UserId)
                                    select m.PartyIndex).ToHashSet();
                                ushort num = aggregate.GetMember(party.LeaderUserId)?.PartyIndex ?? 0;
                                ushort index = (ushort)((num > 0 && num <= 10 && !occupied.Contains(num)) ? num : 0);
                                if (index == 0)
                                {
                                    index = (from i in Enumerable.Range(1, 10)
                                        select (ushort)i).FirstOrDefault((ushort i) => !occupied.Contains(i));
                                }
                                if (index == 0 || aggregate.Members.Where((RaidMember m) => ids.Contains(m.UserId)).All((RaidMember m) => m.PartyIndex == index))
                                {
                                    return false;
                                }
                                foreach (RaidMember item in aggregate.Members.Where((RaidMember m) => ids.Contains(m.UserId)))
                                {
                                    item.PartyIndex = index;
                                }
                                aggregate.AssignmentVersion = Guid.NewGuid();
                                raid = aggregate.Snapshot();
                                return true;
                            }
                        }
                        return false;
                    }
                }
                return false;
            }
        }

        internal bool TryAssignLiveParty(RaidSnapshot expected, ushort actor, Guid actorSession, ushort target, uint index, Func<IReadOnlyList<RaidMember>, bool> commit, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (expected != null && commit != null && index <= 10 && TryGetAggregate(actor, out var current) && !(current.InstanceId != expected.InstanceId) && !(current.AssignmentVersion != expected.AssignmentVersion) && current.LeaderUserId == actor)
                {
                    RaidMember member = current.GetMember(actor);
                    if (member != null && !(member.SessionId != actorSession) && !current.StartPending && (current.State == 2 || current.State == 5) && current.State == expected.State && current.PhaseIndex == expected.PhaseIndex && current.Members.Count == expected.Members.Count && expected.Members.All((RaidMember e) => current.Members.Any((RaidMember m) => m.UserId == e.UserId && m.SessionId == e.SessionId && m.PartyIndex == e.PartyIndex)))
                    {
                        RaidMember member2 = current.GetMember(target);
                        if (member2 == null || (index != 0 && current.Members.Count((RaidMember m) => m.UserId != target && m.PartyIndex == index) >= 4))
                        {
                            return false;
                        }
                        if (!commit(current.Snapshot().Members))
                        {
                            return false;
                        }
                        if (member2.PartyIndex != index)
                        {
                            current.AssignmentVersion = Guid.NewGuid();
                        }
                        member2.PartyIndex = (ushort)index;
                        raid = current.Snapshot();
                        return true;
                    }
                }
                return false;
            }
        }

        public bool TryBeginStart(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var raid2) || raid2.LeaderUserId != userId || raid2.State != 0 || raid2.StartPending)
                {
                    raid = null;
                    return false;
                }
                BeginPreparationLocked(raid2);
                raid = raid2.Snapshot();
                return true;
            }
        }

        private static bool PreparationIsCurrent(RaidAggregate aggregate)
        {
            if (aggregate.StartPending && (aggregate.State == 0 || (aggregate.State == 5 && aggregate.StateArgument == 0 && aggregate.PhaseIndex == 0)) && aggregate.Members.Count == aggregate.PreparationMembers.Count)
            {
                return aggregate.PreparationMembers.All((RaidMember saved) => aggregate.Members.Any((RaidMember current) => current.UserId == saved.UserId && current.SessionId == saved.SessionId && current.PartyIndex == saved.PartyIndex));
            }
            return false;
        }

        public bool TryCommitPreparationResponse(ushort leaderId, Guid leaderSession, ushort memberId, Guid memberSession, Func<IReadOnlyList<RaidMember>, bool> commit)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(memberId, out var raid) || !PreparationIsCurrent(raid) || raid.PreparationResponses.Contains(memberId))
                {
                    return false;
                }
                RaidMember member = raid.PreparationMembers.FirstOrDefault((RaidMember x) => x.UserId == memberId);
                if (member == null || member.SessionId != memberSession || member.PartyIndex == 0)
                {
                    return false;
                }
                RaidMember[] array = raid.PreparationMembers.Where((RaidMember x) => x.PartyIndex == member.PartyIndex).ToArray();
                if (array.Length < 2 || array.Length > 4 || array[0].UserId != leaderId || array[0].SessionId != leaderSession || leaderId == memberId)
                {
                    return false;
                }
                if (!commit(array))
                {
                    return false;
                }
                raid.PreparationResponses.Add(memberId);
                return true;
            }
        }

        public bool IsPreparationReady(RaidSnapshot expected, Func<IReadOnlyList<RaidMember>, bool> partiesReady)
        {
            lock (_lock)
            {
                RaidAggregate value;
                return _raids.TryGetValue(expected.RaidId, out value) && value.InstanceId == expected.InstanceId && value.PreparationGeneration == expected.PreparationGeneration && PreparationIsCurrent(value) && partiesReady(value.PreparationMembers);
            }
        }

        public bool TryCancelPreparation(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (!_raids.TryGetValue(expected.RaidId, out var value) || value.InstanceId != expected.InstanceId || value.PreparationGeneration != expected.PreparationGeneration || !value.StartPending || value.State != expected.State)
                {
                    return false;
                }
                value.StartPending = false;
                raid = value.Snapshot();
                return true;
            }
        }

        public bool TryCompletePreparation(RaidSnapshot expected, out RaidSnapshot raid)
        {
            return TryCompletePreparation(expected, () => true, out raid);
        }

        internal bool TryCompletePreparation(RaidSnapshot expected, Func<bool> commitEntryCosts, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (expected == null || commitEntryCosts == null || !_raids.TryGetValue(expected.RaidId, out var value) || value.InstanceId != expected.InstanceId || value.PreparationGeneration != expected.PreparationGeneration || value.LeaderUserId != expected.LeaderUserId || value.State != 0 || !PreparationIsCurrent(value) || !commitEntryCosts())
                {
                    return false;
                }
                return TryCompleteStart(expected.RaidId, expected.LeaderUserId, out raid);
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
        public bool TryClearDungeon(ushort userId, uint dungeonId, uint maxClearCount, out RaidSnapshot raid, out IReadOnlyList<uint> memberKeys, out uint clearCount)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                clearCount = 0u;
                if (dungeonId == 0 || maxClearCount == 0 || !TryGetAggregate(userId, out var raid2) || raid2.State != 2 || !_dungeonParticipations.TryGetValue(raid2.RaidId, out var value))
                {
                    return false;
                }
                RaidDungeonParticipation raidDungeonParticipation = null;
                foreach (RaidDungeonParticipation item in value)
                {
                    if (item.DungeonId == dungeonId && item.MemberKeys.Contains(userId))
                    {
                        raidDungeonParticipation = item;
                        break;
                    }
                }
                if (raidDungeonParticipation == null)
                {
                    return false;
                }
                if (raidDungeonParticipation.Cleared)
                {
                    return false;
                }
                raidDungeonParticipation.Cleared = true;
                if (!_dungeonClearCounts.TryGetValue(raid2.RaidId, out var value2))
                {
                    value2 = new Dictionary<uint, uint>();
                    _dungeonClearCounts.Add(raid2.RaidId, value2);
                }
                value2.TryGetValue(dungeonId, out var value3);
                clearCount = Math.Min(maxClearCount, value3 + 1);
                value2[dungeonId] = clearCount;
                if (!_clearParticipants.TryGetValue(raid2.RaidId, out var value4))
                {
                    value4 = new HashSet<ushort>();
                    _clearParticipants.Add(raid2.RaidId, value4);
                }
                checked
                {
                    foreach (uint memberKey in raidDungeonParticipation.MemberKeys)
                    {
                        value4.Add((ushort)memberKey);
                        raid2.RecordMemberClear((ushort)memberKey);
                    }
                    raid = raid2.Snapshot();
                    memberKeys = raidDungeonParticipation.MemberKeys;
                    return true;
                }
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
            uint maximumRemainingSeconds,
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
				var totalSeconds = (ulong)baseDurationSeconds + aggregate.PhaseTimeExtensionSeconds;
				var currentRemaining = totalSeconds > elapsedSeconds
					? totalSeconds - elapsedSeconds
					: 0UL;
				var availableRoom = currentRemaining >= maximumRemainingSeconds
					? 0UL
					: maximumRemainingSeconds - currentRemaining;
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

        internal bool TryEnterPhaseBreak(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                RaidAggregate value;
                return _raids.TryGetValue(expected.RaidId, out value) && value.InstanceId == expected.InstanceId && value.PhaseIndex == expected.PhaseIndex && TryEnterPhaseBreak(expected.RaidId, out raid);
            }
        }

        internal bool TryFailPhase(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                RaidAggregate value;
                return _raids.TryGetValue(expected.RaidId, out value) && value.InstanceId == expected.InstanceId && TryFailPhase(expected.RaidId, expected.PhaseIndex, out raid);
            }
        }

        internal bool TryFailAndDisband(RaidSnapshot expected, out RaidSnapshot failed)
        {
            lock (_lock)
            {
                failed = null;
                if (expected == null || !_raids.TryGetValue(expected.RaidId, out var value) || value.InstanceId != expected.InstanceId || !TryFailPhase(expected.RaidId, expected.PhaseIndex, out failed))
                {
                    return false;
                }
                foreach (RaidMember member in value.Members)
                {
                    RemoveWaitingLocked(member.UserId);
                    _userToRaid.Remove(member.UserId);
                    _sessionToUser.Remove(member.SessionId);
                }
                _raids.Remove(value.RaidId);
                _dungeonParticipations.Remove(value.RaidId);
                _dungeonClearCounts.Remove(value.RaidId);
                _clearParticipants.Remove(value.RaidId);
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

        public bool TryCompletePhase(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (expected == null
                    || !_raids.TryGetValue(expected.RaidId, out var aggregate)
                    || aggregate.InstanceId != expected.InstanceId
                    || aggregate.State != 3)
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

        public bool TryPrepareNextPhase(ushort leaderUserId, out RaidSnapshot raid, Func<IReadOnlyList<RaidMember>, IReadOnlyList<RaidMember>> resolveOrder = null)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(leaderUserId, out var raid2) || raid2.LeaderUserId != leaderUserId)
                {
                    raid = null;
                    return false;
                }
                return TryPrepareNextPhaseLocked(raid2, out raid, resolveOrder);
            }
        }

        public bool TryPrepareNextPhaseAutomatically(RaidSnapshot expected, out RaidSnapshot raid, Func<IReadOnlyList<RaidMember>, IReadOnlyList<RaidMember>> resolveOrder = null)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(expected.RaidId, out var value) || value.InstanceId != expected.InstanceId)
                {
                    raid = null;
                    return false;
                }
                return TryPrepareNextPhaseLocked(value, out raid, resolveOrder);
            }
        }

        public bool TryCompletePreparedNextPhase(RaidSnapshot expected, Func<IReadOnlyList<RaidMember>, bool> partiesReady, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(expected.RaidId, out var value) || value.InstanceId != expected.InstanceId || value.PreparationGeneration != expected.PreparationGeneration || !PreparationIsCurrent(value) || value.State != 5 || value.StateArgument != 0 || value.PhaseIndex != 0 || !value.StartPending)
                {
                    raid = null;
                    return false;
                }
                if (partiesReady == null || !partiesReady(value.PreparationMembers))
                {
                    raid = null;
                    return false;
                }
                value.StartPending = false;
                value.State = 2u;
                value.StateArgument = 1u;
                value.PhaseIndex = 1u;
                value.PhaseStartedAtMilliseconds = _clockMilliseconds();
                value.PhaseClearTimeSeconds = 0u;
                value.PhaseTimeExtensionSeconds = 0u;
                value.PhaseDeathCount = 0u;
                _dungeonParticipations.Remove(value.RaidId);
                _dungeonClearCounts.Remove(value.RaidId);
                _clearParticipants.Remove(value.RaidId);
                value.ResetMemberPhaseClears();
                raid = value.Snapshot();
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
            RemoveWaitingLocked(userId);
            if (!TryGetAggregate(userId, out var raid))
            {
                return null;
            }
            RaidSnapshot previousRaid = raid.Snapshot();
            RaidMember member = raid.GetMember(userId);
            raid.RemoveMember(userId);
            _userToRaid.Remove(userId);
            if (member != null)
            {
                _sessionToUser.Remove(member.SessionId);
            }
            if (raid.Members.Count == 0 || raid.LeaderUserId == userId)
            {
                foreach (RaidMember member2 in raid.Members)
                {
                    _userToRaid.Remove(member2.UserId);
                    _sessionToUser.Remove(member2.SessionId);
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
                    PreviousRaid = previousRaid
                };
            }
            return new RaidLeaveResult
            {
                Ok = true,
                RaidId = raid.RaidId,
                PreviousRaid = previousRaid,
                RemainingRaid = raid.Snapshot()
            };
        }

        private bool TryGetAggregate(ushort userId, out RaidAggregate raid)
        {
            if (_userToRaid.TryGetValue(userId, out var raidId) && _raids.TryGetValue(raidId, out raid))
                return true;
            raid = null;
            return false;
        }

        private void BeginPreparationLocked(RaidAggregate aggregate)
        {
            aggregate.StartPending = true;
            aggregate.PreparationGeneration = ++_nextPreparationGeneration;
            aggregate.PreparationMembers = aggregate.Members.Select((RaidMember member) => member.Clone()).ToArray();
            aggregate.PreparationResponses.Clear();
        }

        private bool TryPrepareNextPhaseLocked(RaidAggregate aggregate, out RaidSnapshot raid, Func<IReadOnlyList<RaidMember>, IReadOnlyList<RaidMember>> resolveOrder)
        {
            if (aggregate.State != 5 || aggregate.StateArgument != 0 || aggregate.PhaseIndex != 0 || aggregate.StartPending)
            {
                raid = null;
                return false;
            }
            if (resolveOrder != null)
            {
                IReadOnlyList<RaidMember> readOnlyList = resolveOrder(aggregate.Members.Select((RaidMember m) => m.Clone()).ToArray());
                if (readOnlyList == null || readOnlyList.Count != aggregate.Members.Count || readOnlyList.Any((RaidMember m) => m == null) || readOnlyList.Select((RaidMember m) => m.UserId).Distinct().Count() != readOnlyList.Count || readOnlyList.Any((RaidMember m) => !aggregate.Members.Any((RaidMember e) => e.UserId == m.UserId && e.CharacterId == m.CharacterId && e.SessionId == m.SessionId && e.PartyIndex == m.PartyIndex)))
                {
                    raid = null;
                    return false;
                }
                if (!readOnlyList.Select((RaidMember m) => m.UserId).SequenceEqual(aggregate.Members.Select((RaidMember m) => m.UserId)))
                {
                    aggregate.ApplyPreparationOrder(readOnlyList);
                }
            }
            BeginPreparationLocked(aggregate);
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
