using System;
using System.Collections.Generic;

namespace DfoServer.Game.Raid
{
    public sealed class RaidMember
    {
        public ushort UserId { get; init; }
        public uint CharacterId { get; init; }
        public Guid SessionId { get; internal set; }
        public byte[] NameBytes { get; init; } = Array.Empty<byte>();
        public byte Job { get; init; }
        public byte GrowType { get; init; }
        public ushort PartyIndex { get; internal set; }

        public uint PhaseClearCount { get; internal set; }

        internal RaidMember Clone()
        {
            return new RaidMember
            {
                UserId = UserId,
                CharacterId = CharacterId,
                SessionId = SessionId,
                NameBytes = (byte[])NameBytes.Clone(),
                Job = Job,
                GrowType = GrowType,
                PartyIndex = PartyIndex,
                PhaseClearCount = PhaseClearCount
            };
        }
    }

    public sealed class RaidSnapshot
    {
        public Guid AssignmentVersion { get; init; }

        public Guid LeadershipVersion { get; init; }

        public Guid InstanceId { get; init; }

        public uint RaidId { get; init; }
        public byte[] TitleBytes { get; init; } = Array.Empty<byte>();
        public uint State { get; init; }
        public uint StateArgument { get; init; }
        public uint PhaseIndex { get; init; }
        public uint PhaseClearTimeSeconds { get; init; }
        public uint PhaseTimeExtensionSeconds { get; init; }
        public uint PhaseDeathCount { get; init; }
        public ushort LeaderUserId { get; init; }
        public long PreparationGeneration { get; init; }

        public IReadOnlyList<RaidMember> Members { get; init; } = Array.Empty<RaidMember>();

        public RaidMember Leader
        {
            get
            {
                foreach (var member in Members)
                    if (member.UserId == LeaderUserId)
                        return member;
                return Members.Count > 0 ? Members[0] : null;
            }
        }
    }

    public sealed class RaidSituationGroup
    {
        public ushort SituationIndex { get; init; }
        public ushort PartyIndex { get; init; }
        public IReadOnlyList<uint> MemberKeys { get; init; } = Array.Empty<uint>();
        public uint DungeonId { get; init; }
        public bool DungeonCleared { get; init; }
        public uint UsedCoinCount { get; init; }
        public uint GrantedCoinCount { get; init; }
        public bool IsSolo => PartyIndex == 0;
    }

    internal sealed class RaidAggregate
    {
        private readonly List<RaidMember> _members = new List<RaidMember>(20);

        private readonly Dictionary<uint, uint> _memberPhaseClears = new Dictionary<uint, uint>();

        public Guid InstanceId { get; } = Guid.NewGuid();

        public Guid AssignmentVersion { get; set; } = Guid.NewGuid();

        public Guid LeadershipVersion { get; set; } = Guid.NewGuid();

        public uint RaidId { get; }
        public byte[] TitleBytes { get; set; }
        public uint State { get; set; }
        public uint StateArgument { get; set; }
        public uint PhaseIndex { get; set; }
        public bool StartPending { get; set; }
        public long PreparationGeneration { get; set; }

        public IReadOnlyList<RaidMember> PreparationMembers { get; set; } = Array.Empty<RaidMember>();

        public HashSet<ushort> PreparationResponses { get; } = new HashSet<ushort>();

        public long PhaseStartedAtMilliseconds { get; set; } = -1;
        public uint PhaseClearTimeSeconds { get; set; }
        public uint PhaseTimeExtensionSeconds { get; set; }
        public uint PhaseDeathCount { get; set; }
        public ushort LeaderUserId { get; set; }
        public IReadOnlyList<RaidMember> Members => _members;

        internal void RecordMemberClear(ushort userId)
        {
            RaidMember member = GetMember(userId);
            if (member != null)
            {
                _memberPhaseClears.TryGetValue(member.CharacterId, out var value);
                _memberPhaseClears[member.CharacterId] = ((value == uint.MaxValue) ? value : (value + 1));
            }
        }

        internal void ResetMemberPhaseClears()
        {
            _memberPhaseClears.Clear();
        }

        public RaidAggregate(uint raidId, byte[] titleBytes, RaidMember leader)
        {
            RaidId = raidId;
            TitleBytes = titleBytes ?? Array.Empty<byte>();
            LeaderUserId = leader.UserId;
            _members.Add(leader);
        }

        public RaidMember GetMember(ushort userId)
        {
            foreach (var member in _members)
                if (member.UserId == userId)
                    return member;
            return null;
        }

        public bool AddMember(RaidMember member)
        {
            if (member == null || _members.Count >= 20 || GetMember(member.UserId) != null)
            {
                return false;
            }
            _members.Add(member);
            AssignmentVersion = Guid.NewGuid();
            return true;
        }

        public bool RemoveMember(ushort userId)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].UserId == userId)
                {
                    _members.RemoveAt(i);
                    AssignmentVersion = Guid.NewGuid();
                    return true;
                }
            }
            return false;
        }

        internal void ApplyPreparationOrder(IReadOnlyList<RaidMember> ordered)
        {
            Dictionary<ushort, RaidMember> dictionary = new Dictionary<ushort, RaidMember>();
            foreach (RaidMember member in _members)
            {
                dictionary.Add(member.UserId, member);
            }
            _members.Clear();
            foreach (RaidMember item in ordered)
            {
                _members.Add(dictionary[item.UserId]);
            }
            AssignmentVersion = Guid.NewGuid();
        }

        public RaidSnapshot Snapshot()
        {
            List<RaidMember> list = new List<RaidMember>(_members.Count);
            foreach (RaidMember member in _members)
            {
                RaidMember raidMember = member.Clone();
                _memberPhaseClears.TryGetValue(member.CharacterId, out var value);
                raidMember.PhaseClearCount = value;
                list.Add(raidMember);
            }
            return new RaidSnapshot
            {
                RaidId = RaidId,
                InstanceId = InstanceId,
                AssignmentVersion = AssignmentVersion,
                LeadershipVersion = LeadershipVersion,
                TitleBytes = (byte[])TitleBytes.Clone(),
                State = State,
                StateArgument = StateArgument,
                PhaseIndex = PhaseIndex,
                PhaseClearTimeSeconds = PhaseClearTimeSeconds,
                PhaseTimeExtensionSeconds = PhaseTimeExtensionSeconds,
                PhaseDeathCount = PhaseDeathCount,
                LeaderUserId = LeaderUserId,
                PreparationGeneration = PreparationGeneration,
                Members = list
            };
        }
    }
}
