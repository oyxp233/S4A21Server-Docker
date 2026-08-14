using System;
using System.Collections.Generic;

namespace DfoServer.Game.Raid
{
    public sealed class RaidMember
    {
        public ushort UserId { get; init; }
        public uint CharacterId { get; init; }
        public Guid SessionId { get; init; }
        public byte[] NameBytes { get; init; } = Array.Empty<byte>();
        public byte Job { get; init; }
        public byte GrowType { get; init; }
        public ushort PartyIndex { get; internal set; }

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
            };
        }
    }

    public sealed class RaidSnapshot
    {
        public uint RaidId { get; init; }
        public byte[] TitleBytes { get; init; } = Array.Empty<byte>();
        public uint State { get; init; }
        public uint StateArgument { get; init; }
        public uint PhaseIndex { get; init; }
        public uint PhaseClearTimeSeconds { get; init; }
        public uint PhaseTimeExtensionSeconds { get; init; }
        public uint PhaseDeathCount { get; init; }
        public ushort LeaderUserId { get; init; }
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

        public uint RaidId { get; }
        public byte[] TitleBytes { get; set; }
        public uint State { get; set; }
        public uint StateArgument { get; set; }
        public uint PhaseIndex { get; set; }
        public bool StartPending { get; set; }
        public long PhaseStartedAtMilliseconds { get; set; } = -1;
        public uint PhaseClearTimeSeconds { get; set; }
        public uint PhaseTimeExtensionSeconds { get; set; }
        public uint PhaseDeathCount { get; set; }
        public ushort LeaderUserId { get; set; }
        public IReadOnlyList<RaidMember> Members => _members;

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

        public bool RemoveMember(ushort userId)
        {
            for (var i = 0; i < _members.Count; i++)
            {
                if (_members[i].UserId != userId)
                    continue;
                _members.RemoveAt(i);
                return true;
            }
            return false;
        }

        public RaidSnapshot Snapshot()
        {
            var members = new List<RaidMember>(_members.Count);
            foreach (var member in _members)
                members.Add(member.Clone());

            return new RaidSnapshot
            {
                RaidId = RaidId,
                TitleBytes = (byte[])TitleBytes.Clone(),
                State = State,
                StateArgument = StateArgument,
                PhaseIndex = PhaseIndex,
                PhaseClearTimeSeconds = PhaseClearTimeSeconds,
                PhaseTimeExtensionSeconds = PhaseTimeExtensionSeconds,
                PhaseDeathCount = PhaseDeathCount,
                LeaderUserId = LeaderUserId,
                Members = members,
            };
        }
    }
}
