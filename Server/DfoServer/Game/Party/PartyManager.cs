using System.Collections.Generic;
using System;
using System.Linq;
using DfoServer.Game.Raid;

namespace DfoServer.Game.Party
{
    /// <summary>组队操作的结果, 供 handler 决定向谁下发什么封包(格式无关)。</summary>
    public sealed class PartyOpResult
    {
        public bool Ok { get; set; }
        public string Reason { get; set; } = string.Empty;
        public Party Party { get; set; }

        /// <summary>
        /// The client generation that was retired by this operation.
        /// A non-null value means survivors must clear this party id before
        /// receiving a fresh formation for <see cref="Party"/>.
        /// </summary>
        public Party RetiredParty { get; set; }

        /// <summary>受影响的目标 UserId(加入/离开/被踢者)。</summary>
        public ushort TargetUserId { get; set; }

        /// <summary>队伍是否已解散(成员清空后移除)。</summary>
        public bool Disbanded { get; set; }

        /// <summary>副本回城时最后一名成员保留为单人队。</summary>
        public bool SoleMemberPreserved { get; set; }

        /// <summary>团本准备握手已在原队伍中完成，不重复发布成员变化。</summary>
        public bool MembershipUnchanged { get; set; }

        /// <summary>队长是否变更(队长离队时转移)。</summary>
        public bool LeaderChanged { get; set; }
        public ushort NewLeaderUserId { get; set; }

        /// <summary>操作后仍在队且需被通知的成员快照(离开/踢人时不含目标本人)。</summary>
        public List<PartyMember> RemainingMembers { get; set; } = new List<PartyMember>();

        /// <summary>建队/入队时若目标玩家原本在别的队伍, 这里带出原队的离队结果——
        /// 原队剩余成员需要收到离队通知, 不能被静默吞掉。null 表示原本无队。</summary>
        public PartyOpResult PriorPartyLeave { get; set; }

        public static PartyOpResult Fail(string reason) => new PartyOpResult { Ok = false, Reason = reason };
    }

    /// <summary>
    /// 组队生命周期与注册表(线程安全, 格式无关)。不负责封包收发 —— handler 查询本管理器后自行构建/下发。
    /// 队伍按分配的 PartyId 索引; 成员按 UserId(=CharacterId 截断)索引到所属队伍。
    /// </summary>
    public sealed class PartyManager
    {
        internal PartyOpResult SetPartyInfo(PartyMember leader, byte[] title, byte userMax, byte[] block, IReadOnlyList<RaidMember> raidRoster, out PartyOpResult created)
        {
            created = null;
            if (leader == null || leader.SessionId == Guid.Empty)
            {
                return PartyOpResult.Fail("invalid_member_identity");
            }

            if (!PartyConstants.IsSupportedCapacity(userMax) || block == null || block.Length != 12 || block[2] != userMax)
            {
                return PartyOpResult.Fail("invalid_party_info");
            }

            lock (_lock)
            {
                Party partyByUser = GetPartyByUser(leader.UserId);
                if (raidRoster != null)
                {
                    if (!raidRoster.Any((RaidMember m) => m.UserId == leader.UserId && m.SessionId == leader.SessionId) || (partyByUser != null && partyByUser.Members.Any((PartyMember p) => !raidRoster.Any((RaidMember m) => m.UserId == p.UserId && m.SessionId == p.SessionId))))
                    {
                        return PartyOpResult.Fail("raid_party_conflict");
                    }

                    userMax = 4;
                    block = new byte[12]
                    {
                        0,
                        0,
                        4,
                        255,
                        255,
                        255,
                        255,
                        5,
                        0,
                        2,
                        0,
                        0
                    };
                }

                if (partyByUser == null)
                {
                    created = CreateParty(leader);
                }

                PartyOpResult partyOpResult = UpdateSettings(leader.UserId, leader.SessionId, title, userMax, block);
                if (partyOpResult.Ok && raidRoster != null)
                {
                    partyOpResult.Party.IsSinglePlay = false;
                }

                return partyOpResult;
            }
        }

        internal PartyOpResult AcceptRaidAwareInvite(PartyMember inviter, PartyMember invitee, IReadOnlyList<RaidMember> roster, out string mode)
        {
            mode = null;
            if (inviter == null || invitee == null)
                return PartyOpResult.Fail("invalid_member_identity");
            lock (_lock)
            {
                if (roster != null)
                {
                    var members = new[]
                    {
                        inviter,
                        invitee
                    }.Concat(GetPartyByUser(inviter.UserId)?.Members ?? Enumerable.Empty<PartyMember>()).Concat(GetPartyByUser(invitee.UserId)?.Members ?? Enumerable.Empty<PartyMember>()).ToArray();
                    if (members.Select(m => m.UserId).Distinct().Count() > 4 || members.Any(p => !roster.Any(r => r.UserId == p.UserId && r.CharacterId == p.CharacterId && r.SessionId == p.SessionId)))
                        return PartyOpResult.Fail("raid_party_conflict");
                }

                var result = AcceptInvite(invitee.UserId, invitee.SessionId, inviter.UserId, inviter.SessionId, inviter, invitee, out mode);
                if (result.Ok && roster != null)
                {
                    // Commit canonical raid settings before any membership notification can be sent.
                    // The actual leader may be the invitee when a solo player applies to a party.
                    var party = result.Party;
                    party.UserMax = 4;
                    party.IsSinglePlay = false;
                    party.PartyInfoBlock = new byte[]
                    {
                        0,
                        0,
                        4,
                        255,
                        255,
                        255,
                        255,
                        5,
                        0,
                        2,
                        0,
                        0
                    };
                }

                return result;
            }
        }

        internal PartyOpResult AcceptPreparedRaidMember(PartyMember leader, PartyMember member, IReadOnlyList<RaidMember> expected)
        {
            lock (_lock)
            {
                if (leader == null || member == null || expected == null || expected.Count < 2 || expected.Count > 4 || expected[0].UserId != leader.UserId || expected[0].SessionId != leader.SessionId || leader.UserId == member.UserId || !expected.Any((RaidMember e) => e.UserId == member.UserId && e.SessionId == member.SessionId))
                {
                    return PartyOpResult.Fail("raid_invalid_member_identity");
                }

                Party partyByUser = GetPartyByUser(leader.UserId);
                Party partyByUser2 = GetPartyByUser(member.UserId);
                if (partyByUser2 != null && partyByUser2 != partyByUser)
                {
                    return PartyOpResult.Fail("raid_member_already_in_party");
                }

                if (partyByUser != null && (partyByUser.LeaderUserId != leader.UserId || partyByUser.Members.Any((PartyMember p) => !expected.Any((RaidMember e) => e.UserId == p.UserId && e.SessionId == p.SessionId))))
                {
                    return PartyOpResult.Fail("raid_party_conflict");
                }

                if (partyByUser2 != null)
                {
                    PartyOpResult partyOpResult = UpdateSettings(leader.UserId, leader.SessionId, partyByUser.TitleBytes, 4, new byte[12] { 0, 0, 4, 255, 255, 255, 255, 5, 0, 2, 0, 0 });
                    partyOpResult.MembershipUnchanged = true;
                    return partyOpResult;
                }

                if (partyByUser != null && partyByUser.IsFull)
                {
                    return PartyOpResult.Fail("raid_party_full");
                }

                if (_pendingInvites.ContainsKey(member.UserId))
                {
                    return PartyOpResult.Fail("raid_pending_conflict");
                }

                if (!RecordInvite(member.UserId, member.SessionId, leader.UserId, leader.SessionId, out var failureReason))
                {
                    return PartyOpResult.Fail(failureReason);
                }

                PartyOpResult partyOpResult2 = AcceptInvite(member.UserId, member.SessionId, leader.UserId, leader.SessionId, leader, member, out var _);
                if (partyOpResult2.Ok)
                {
                    UpdateSettings(leader.UserId, leader.SessionId, partyOpResult2.Party.TitleBytes, 4, new byte[12] { 0, 0, 4, 255, 255, 255, 255, 5, 0, 2, 0, 0 });
                }

                return partyOpResult2;
            }
        }

        internal bool ReassignLiveRaidMember(IReadOnlyList<RaidMember> roster, PartyMember moving, ushort destination, out List<Party> retired, out List<Party> formed)
        {
            lock (_lock)
            {
                retired = new List<Party>();
                formed = new List<Party>();
                RaidMember target = roster?.FirstOrDefault((RaidMember raidMember) => raidMember.UserId == moving?.UserId);
                if (target == null || target.SessionId != moving.SessionId || destination > 10)
                {
                    return false;
                }

                RaidMember[] array = roster.Where((RaidMember raidMember) => raidMember.UserId == target.UserId || (target.PartyIndex != 0 && raidMember.PartyIndex == target.PartyIndex) || (destination != 0 && raidMember.PartyIndex == destination)).ToArray();
                if (array.Any((RaidMember raidMember) => _pendingInvites.ContainsKey(raidMember.UserId)))
                {
                    return false;
                }

                Dictionary<ushort, PartyMember> actualMembers = new Dictionary<ushort, PartyMember>();
                RaidMember[] array2 = array;
                foreach (RaidMember m in array2)
                {
                    Party party = GetPartyByUser(m.UserId);
                    if (m.PartyIndex == 0)
                    {
                        if (party != null)
                        {
                            return false;
                        }

                        actualMembers[m.UserId] = CloneMember(moving);
                        continue;
                    }

                    RaidMember[] group = roster.Where((RaidMember e) => e.PartyIndex == m.PartyIndex).ToArray();
                    if (party != null && party.Count == group.Length)
                    {
                        byte[] partyInfoBlock = party.PartyInfoBlock;
                        if (partyInfoBlock != null && partyInfoBlock.Length == 12 && party.PartyInfoBlock[9] == 2 && party.Members.All((PartyMember p) => group.Any((RaidMember e) => e.UserId == p.UserId && e.SessionId == p.SessionId)))
                        {
                            actualMembers[m.UserId] = CloneMember(party.GetMember(m.UserId));
                            if (!retired.Any((Party p) => p.PartyId == party.PartyId))
                            {
                                retired.Add(party.CreateSnapshot());
                            }

                            continue;
                        }
                    }

                    return false;
                }

                if (target.PartyIndex == destination)
                {
                    retired.Clear();
                    return true;
                }

                IGrouping<ushort, RaidMember>[] array3 = (
                    from raidMember in array
                    group raidMember by (raidMember.UserId == target.UserId) ? destination : raidMember.PartyIndex into g
                        where g.Key != 0
                        select g).ToArray();
                if (array3.Any((IGrouping<ushort, RaidMember> g) => g.Count() > 4))
                {
                    return false;
                }

                IGrouping<ushort, RaidMember>[] array4 = array3;
                foreach (IGrouping<ushort, RaidMember> source in array4)
                {
                    PartyMember[] array5 = source.Select((RaidMember raidMember) => actualMembers[raidMember.UserId]).ToArray();
                    Party source2 = GetPartyByUser(source.First().UserId) ?? new Party(0);
                    Party party2 = CreateReplacementPartyLocked(source2, array5[0], array5);
                    party2.IsSinglePlay = false;
                    party2.UserMax = 4;
                    party2.PartyInfoBlock = new byte[12]
                    {
                        0,
                        0,
                        4,
                        255,
                        255,
                        255,
                        255,
                        5,
                        0,
                        2,
                        0,
                        0
                    };
                    formed.Add(party2);
                }

                foreach (Party item in retired)
                {
                    _parties.Remove(item.PartyId);
                    foreach (PartyMember member in item.Members)
                    {
                        _userToParty.Remove(member.UserId);
                    }
                }

                foreach (Party item2 in formed)
                {
                    _parties.Add(item2.PartyId, item2);
                    foreach (PartyMember member2 in item2.Members)
                    {
                        _userToParty[member2.UserId] = item2.PartyId;
                    }
                }

                formed = formed.Select((Party p) => p.CreateSnapshot()).ToList();
                return true;
            }
        }

        internal IReadOnlyList<RaidMember> ResolveRaidPreparationOrder(IReadOnlyList<RaidMember> members)
        {
            lock (_lock)
            {
                RaidMember[] array = members.Select((RaidMember m) => m.Clone()).ToArray();
                foreach (IGrouping<ushort, RaidMember> group in
                    from m in members
                    where m.PartyIndex != 0
                    group m by m.PartyIndex)
                {
                    Party party = GetPartyByUser(group.First().UserId);
                    if (party == null || party.Count != group.Count() || !party.Members.All((PartyMember p) => group.Any((RaidMember m) => m.UserId == p.UserId && m.CharacterId == p.CharacterId && m.SessionId == p.SessionId)))
                    {
                        return null;
                    }

                    int num = Array.FindIndex(array, (RaidMember m) => m.PartyIndex == group.Key);
                    int num2 = Array.FindIndex(array, (RaidMember m) => m.PartyIndex == group.Key && m.UserId == party.LeaderUserId);
                    if (num2 < 0)
                    {
                        return null;
                    }

                    ref RaidMember reference = ref array[num];
                    ref RaidMember reference2 = ref array[num2];
                    RaidMember raidMember = array[num2];
                    RaidMember raidMember2 = array[num];
                    reference = raidMember;
                    reference2 = raidMember2;
                }

                return array;
            }
        }

        internal bool ArePreparedRaidPartiesReady(IReadOnlyList<RaidMember> members)
        {
            lock (_lock)
            {
                foreach (IGrouping<ushort, RaidMember> group in
                    from x in members
                    where x.PartyIndex != 0
                    group x by x.PartyIndex)
                {
                    RaidMember raidMember = group.First();
                    Party partyByUser = GetPartyByUser(raidMember.UserId);
                    if (partyByUser == null || partyByUser.LeaderUserId != raidMember.UserId || partyByUser.Count != group.Count() || partyByUser.Members.Any((PartyMember p) => !group.Any((RaidMember e) => e.UserId == p.UserId && e.SessionId == p.SessionId)) || partyByUser.PartyInfoBlock == null || partyByUser.PartyInfoBlock.Length != 12 || partyByUser.PartyInfoBlock[9] != 2)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private readonly object _lock = new object();
        private readonly Dictionary<int, Party> _parties = new Dictionary<int, Party>();
        private readonly Dictionary<ushort, int> _userToParty = new Dictionary<ushort, int>();
        // 待应答的组队邀请按双方会话代际绑定。旧连接伪造/重放 RES_PEER
        // 不能消费新连接收到的邀请，也不能把接受者加入邀请者后来重建的队伍。
        // 同一被邀请者只保留最后一次邀请。
        private readonly Dictionary<ushort, PendingPartyInvite> _pendingInvites
            = new Dictionary<ushort, PendingPartyInvite>();
        private int _nextPartyId = 1;

        /// <summary>查询某玩家所属队伍; 不在任何队伍返回 null。</summary>
        public Party GetPartyByUser(ushort userId)
        {
            lock (_lock)
            {
                if (_userToParty.TryGetValue(userId, out var pid) && _parties.TryGetValue(pid, out var party))
                    return party;
                return null;
            }
        }

        public Party GetPartyById(int partyId)
        {
            lock (_lock)
            {
                return _parties.TryGetValue(partyId, out var party) ? party : null;
            }
        }

        public Party GetPartySnapshot(int partyId)
        {
            lock (_lock)
            {
                return _parties.TryGetValue(partyId, out var party)
                    ? party.CreateSnapshot()
                    : null;
            }
        }

        public bool TryUpdateMemberP2pPort(
            ushort userId, System.Guid sessionId, ushort port, out Party party)
        {
            lock (_lock)
            {
                party = null;
                if (!_userToParty.TryGetValue(userId, out var partyId) ||
                    !_parties.TryGetValue(partyId, out party))
                {
                    return false;
                }

                var member = party.GetMember(userId);
                if (member == null || member.SessionId != sessionId)
                    return false;
                member.P2pPort = port;
                return true;
            }
        }

        public PartyOpResult UpdateSettings(
            ushort leaderUserId,
            System.Guid expectedSessionId,
            byte[] titleBytes,
            byte userMax,
            byte[] partyInfoBlock)
        {
            if (!PartyConstants.IsSupportedCapacity(userMax))
                return PartyOpResult.Fail("invalid_user_max");
            if (partyInfoBlock == null ||
                partyInfoBlock.Length != 12 ||
                partyInfoBlock[2] != userMax)
            {
                return PartyOpResult.Fail("invalid_party_info");
            }

            lock (_lock)
            {
                if (!_userToParty.TryGetValue(leaderUserId, out var partyId) ||
                    !_parties.TryGetValue(partyId, out var party))
                {
                    return PartyOpResult.Fail("not_in_party");
                }

                var leader = party.GetMember(leaderUserId);
                if (leader == null || leader.SessionId != expectedSessionId)
                    return PartyOpResult.Fail("stale_session");
                if (party.LeaderUserId != leaderUserId)
                    return PartyOpResult.Fail("not_leader");
                if (party.Count > userMax)
                    return PartyOpResult.Fail("member_count_exceeds_user_max");

                party.TitleIndex = 0;
                party.TitleBytes = titleBytes == null
                    ? System.Array.Empty<byte>()
                    : (byte[])titleBytes.Clone();
                party.UserMax = userMax;
                party.DungIndex = 0;
                party.DungDiffi = 0;
                party.PartyInfoBlock = (byte[])partyInfoBlock.Clone();
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = leaderUserId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }
        }

        /// <summary>
        /// 创建一支新队伍, leader 成为队长与第一名成员。
        /// 若 leader 已在别的队伍, 先将其从原队移除, 原队的离队结果通过 PriorPartyLeave 带出供通知。
        /// </summary>
        public PartyOpResult CreateParty(PartyMember leader, bool singlePlay = false)
        {
            lock (_lock)
            {
                var prior = LeaveLocked(leader.UserId);

                var party = new Party(_nextPartyId++) { IsSinglePlay = singlePlay };
                party.TryAddMember(leader);
                party.LeaderUserId = leader.UserId;
                _parties[party.PartyId] = party;
                _userToParty[leader.UserId] = party.PartyId;
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = leader.UserId,
                    RemainingMembers = party.MembersBySlot(),
                    PriorPartyLeave = prior != null && prior.Ok ? prior : null,
                };
            }
        }

        /// <summary>把一名成员加入指定队伍。若其已在别的队伍先移除(原队结果经 PriorPartyLeave 带出)。满员/队伍不存在则失败。</summary>
        public PartyOpResult Join(int partyId, PartyMember member)
        {
            lock (_lock)
            {
                if (!_parties.TryGetValue(partyId, out var party))
                    return PartyOpResult.Fail("party_not_found");
                if (party.Contains(member.UserId))
                    return PartyOpResult.Fail("already_member");
                if (party.IsFull)
                    return PartyOpResult.Fail("party_full");

                var prior = LeaveLocked(member.UserId);

                if (!party.TryAddMember(member))
                    return PartyOpResult.Fail("add_failed");
                _userToParty[member.UserId] = party.PartyId;

                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = member.UserId,
                    RemainingMembers = party.MembersBySlot(),
                    PriorPartyLeave = prior != null && prior.Ok ? prior : null,
                };
            }
        }

        /// <summary>
        /// 某玩家离队。若离队后队伍为空则解散; 若离队者是队长则把队长转移给下一名成员。
        /// </summary>
        public PartyOpResult Leave(ushort userId)
        {
            lock (_lock)
            {
                return LeaveLocked(userId) ?? PartyOpResult.Fail("not_in_party");
            }
        }

        public PartyOpResult Leave(
            ushort userId,
            System.Guid expectedSessionId)
        {
            lock (_lock)
            {
                if (!TryGetMemberLocked(
                        userId, out _, out var member) ||
                    member.SessionId != expectedSessionId)
                {
                    return PartyOpResult.Fail("stale_session");
                }

                return LeaveLocked(userId) ??
                       PartyOpResult.Fail("not_in_party");
            }
        }

        public Party GetPartySnapshotByUser(ushort userId)
        {
            lock (_lock)
            {
                return _userToParty.TryGetValue(userId, out var partyId) &&
                       _parties.TryGetValue(partyId, out var party)
                    ? party.CreateSnapshot()
                    : null;
            }
        }

        public PartyOpResult LeaveForDungeonReturn(
            ushort userId,
            System.Guid expectedSessionId,
            int expectedPartyId)
        {
            lock (_lock)
            {
                if (!TryGetMemberLocked(
                        userId, out var party, out var member) ||
                    member.SessionId != expectedSessionId)
                {
                    return PartyOpResult.Fail("stale_session");
                }
                if (party.PartyId != expectedPartyId)
                    return PartyOpResult.Fail("party_generation_mismatch");

                if (party.Count > 1)
                {
                    // The active dungeon cohort and its scene ownership keep
                    // the entry PartyId for the lifetime of the instance.
                    // Preserve that generation and every survivor slot while
                    // transferring leadership in place.
                    return LeaveLocked(
                               userId,
                               preservePartyOnLeaderExit: true) ??
                           PartyOpResult.Fail("not_in_party");
                }

                var leaderChanged = party.LeaderUserId != userId;
                party.LeaderUserId = userId;
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = userId,
                    SoleMemberPreserved = true,
                    LeaderChanged = leaderChanged,
                    NewLeaderUserId = userId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }
        }

        public PartyOpResult LeaveExpectedParty(
            ushort userId,
            System.Guid expectedSessionId,
            int expectedPartyId)
        {
            lock (_lock)
            {
                if (!TryGetMemberLocked(
                        userId, out var party, out var member) ||
                    member.SessionId != expectedSessionId)
                {
                    return PartyOpResult.Fail("stale_session");
                }
                if (party.PartyId != expectedPartyId)
                    return PartyOpResult.Fail("party_generation_mismatch");

                // Stale-session cleanup is still part of the same frozen
                // dungeon return operation and must not replace PartyId.
                return LeaveLocked(
                           userId,
                           preservePartyOnLeaderExit: true) ??
                       PartyOpResult.Fail("not_in_party");
            }
        }

        // 已持锁的离队实现。不在任何队伍返回 null。
        // 建队/入队前的自动清理与显式 Leave 共用这一份, 保证队长转移/换槽/解散逻辑只有一处。
        private PartyOpResult LeaveLocked(
            ushort userId,
            bool preservePartyOnLeaderExit = false)
        {
            if (!_userToParty.TryGetValue(userId, out var pid) || !_parties.TryGetValue(pid, out var party))
                return null;

            var wasLeader = party.LeaderUserId == userId;
            var retiredSnapshot =
                wasLeader && party.Count > 1 && !preservePartyOnLeaderExit
                    ? party.CreateSnapshot()
                    : null;
            party.RemoveMember(userId);
            _userToParty.Remove(userId);

            var result = new PartyOpResult { Ok = true, Party = party, TargetUserId = userId };

            if (party.IsEmpty)
            {
                _parties.Remove(party.PartyId);
                result.Disbanded = true;
                return result;
            }

            if (wasLeader)
            {
                var next = party.MembersBySlot()[0];
                if (preservePartyOnLeaderExit)
                {
                    // A disconnected or in-dungeon returning leader leaves
                    // the existing party generation in place. The lowest
                    // occupied slot inherits leadership; every surviving
                    // member keeps the same UI slot.
                    party.LeaderUserId = next.UserId;
                    result.LeaderChanged = true;
                    result.NewLeaderUserId = next.UserId;
                    result.RemainingMembers = party.MembersBySlot();
                    return result;
                }

                var survivors = party.MembersBySlot();

                // PARTY_INFO type=0 is applied by the 86 client as an ordered
                // slot0..7 diff. Reusing the same party id for
                // [oldLeader@0,next@1] -> [next@0,empty@1] first assigns the
                // successor and then clears its member back-reference while
                // processing slot1. Retire the old client generation and
                // rebuild survivors under a fresh id instead.
                _parties.Remove(party.PartyId);
                foreach (var survivor in survivors)
                    _userToParty.Remove(survivor.UserId);

                var rebuilt = CreateReplacementPartyLocked(
                    party,
                    next,
                    survivors);
                _parties[rebuilt.PartyId] = rebuilt;
                foreach (var survivor in rebuilt.Members)
                    _userToParty[survivor.UserId] = rebuilt.PartyId;

                result.Party = rebuilt;
                result.RetiredParty = retiredSnapshot;
                result.LeaderChanged = true;
                result.NewLeaderUserId = next.UserId;
            }

            result.RemainingMembers = result.Party.MembersBySlot();
            return result;
        }

        private Party CreateReplacementPartyLocked(
            Party source,
            PartyMember newLeader,
            IReadOnlyList<PartyMember> orderedMembers)
        {
            var rebuilt = new Party(_nextPartyId++)
            {
                LeaderUserId = newLeader.UserId,
                PartyName = source.PartyName,
                SettingA = source.SettingA,
                SettingB = source.SettingB,
                SettingC = source.SettingC,
                TitleIndex = source.TitleIndex,
                TitleBytes = source.TitleBytes == null
                    ? System.Array.Empty<byte>()
                    : (byte[])source.TitleBytes.Clone(),
                UserMax = source.UserMax,
                DungIndex = source.DungIndex,
                DungDiffi = source.DungDiffi,
                PartyInfoBlock = source.PartyInfoBlock == null
                    ? System.Array.Empty<byte>()
                    : (byte[])source.PartyInfoBlock.Clone(),
                IsSinglePlay = source.IsSinglePlay,
            };

            rebuilt.TryAddMember(CloneMember(newLeader));
            foreach (var member in orderedMembers)
            {
                if (member.UserId == newLeader.UserId)
                    continue;
                rebuilt.TryAddMember(CloneMember(member));
            }

            return rebuilt;
        }

        /// <summary>队长踢人。仅队长可踢, 且不能踢自己(踢自己走 Leave)。</summary>
        public PartyOpResult Kick(ushort byUserId, ushort targetUserId)
        {
            lock (_lock)
            {
                if (!_userToParty.TryGetValue(byUserId, out var pid) || !_parties.TryGetValue(pid, out var party))
                    return PartyOpResult.Fail("not_in_party");
                if (party.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");
                if (byUserId == targetUserId)
                    return PartyOpResult.Fail("cannot_kick_self");
                if (!party.Contains(targetUserId))
                    return PartyOpResult.Fail("target_not_member");

                party.RemoveMember(targetUserId);
                _userToParty.Remove(targetUserId);

                var result = new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = targetUserId,
                    RemainingMembers = party.MembersBySlot(),
                };

                if (party.IsEmpty)
                {
                    _parties.Remove(party.PartyId);
                    result.Disbanded = true;
                }
                return result;
            }
        }

        public PartyOpResult Kick(
            ushort byUserId,
            System.Guid expectedBySessionId,
            ushort targetUserId,
            System.Guid expectedTargetSessionId)
        {
            lock (_lock)
            {
                if (!TryGetMemberLocked(
                        byUserId, out var party, out var byMember) ||
                    byMember.SessionId != expectedBySessionId)
                {
                    return PartyOpResult.Fail("stale_session");
                }
                if (party.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");
                if (byUserId == targetUserId)
                    return PartyOpResult.Fail("cannot_kick_self");

                var target = party.GetMember(targetUserId);
                if (target == null)
                    return PartyOpResult.Fail("target_not_member");
                if (target.SessionId != expectedTargetSessionId)
                    return PartyOpResult.Fail("stale_target_session");

                party.RemoveMember(targetUserId);
                _userToParty.Remove(targetUserId);

                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = targetUserId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }
        }

        public PartyOpResult RebuildWithLeader(
            int partyId,
            ushort byUserId,
            System.Guid expectedBySessionId,
            ushort newLeaderUserId,
            System.Guid expectedNewLeaderSessionId)
        {
            lock (_lock)
            {
                if (!_parties.TryGetValue(partyId, out var oldParty))
                    return PartyOpResult.Fail("party_not_found");
                if (oldParty.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");

                var byMember = oldParty.GetMember(byUserId);
                var newLeader = oldParty.GetMember(newLeaderUserId);
                if (byMember == null ||
                    byMember.SessionId != expectedBySessionId ||
                    newLeader == null ||
                    newLeader.SessionId != expectedNewLeaderSessionId)
                {
                    return PartyOpResult.Fail("stale_session");
                }

                var orderedMembers = oldParty.MembersBySlot();
                var retiredSnapshot = oldParty.CreateSnapshot();
                foreach (var member in orderedMembers)
                    _userToParty.Remove(member.UserId);
                _parties.Remove(partyId);

                var rebuilt = CreateReplacementPartyLocked(
                    oldParty,
                    newLeader,
                    orderedMembers);

                _parties[rebuilt.PartyId] = rebuilt;
                foreach (var member in rebuilt.Members)
                    _userToParty[member.UserId] = rebuilt.PartyId;

                return new PartyOpResult
                {
                    Ok = true,
                    Party = rebuilt,
                    RetiredParty = retiredSnapshot,
                    TargetUserId = newLeaderUserId,
                    LeaderChanged = true,
                    NewLeaderUserId = newLeaderUserId,
                    RemainingMembers = rebuilt.MembersBySlot(),
                };
            }
        }

        public PartyOpResult TransferLeader(
            int partyId,
            ushort byUserId,
            System.Guid expectedBySessionId,
            ushort newLeaderUserId,
            System.Guid expectedNewLeaderSessionId)
        {
            lock (_lock)
            {
                if (!_parties.TryGetValue(partyId, out var party))
                    return PartyOpResult.Fail("party_not_found");
                if (party.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");

                var byMember = party.GetMember(byUserId);
                var newLeader = party.GetMember(newLeaderUserId);
                if (byMember == null ||
                    byMember.SessionId != expectedBySessionId ||
                    newLeader == null ||
                    newLeader.SessionId != expectedNewLeaderSessionId)
                {
                    return PartyOpResult.Fail("stale_session");
                }

                party.LeaderUserId = newLeaderUserId;
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = newLeaderUserId,
                    LeaderChanged = true,
                    NewLeaderUserId = newLeaderUserId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }
        }

        /// <summary>队长手动转移。newLeader 必须是本队成员。</summary>
        public PartyOpResult TransferLeader(ushort byUserId, ushort newLeaderUserId)
        {
            lock (_lock)
            {
                if (!_userToParty.TryGetValue(byUserId, out var pid) || !_parties.TryGetValue(pid, out var party))
                    return PartyOpResult.Fail("not_in_party");
                if (party.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");
                if (!party.Contains(newLeaderUserId))
                    return PartyOpResult.Fail("target_not_member");

                party.LeaderUserId = newLeaderUserId;
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    LeaderChanged = true,
                    NewLeaderUserId = newLeaderUserId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }
        }

        /// <summary>解散整支队伍(清空索引)。返回解散前的成员快照供通知。</summary>
        public PartyOpResult Disband(int partyId)
        {
            lock (_lock)
            {
                if (!_parties.TryGetValue(partyId, out var party))
                    return PartyOpResult.Fail("party_not_found");

                var members = party.MembersBySlot();
                foreach (var m in members)
                    _userToParty.Remove(m.UserId);
                _parties.Remove(partyId);

                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    Disbanded = true,
                    RemainingMembers = members,
                };
            }
        }

        /// <summary>断线清理: 等价于 Leave, 供会话断开时调用。顺带清掉与该玩家相关的待应答邀请。</summary>
        public PartyOpResult OnSessionDisconnected(
            ushort userId, System.Guid sessionId)
        {
            lock (_lock)
            {
                if (sessionId == System.Guid.Empty)
                    return PartyOpResult.Fail("invalid_session");

                // 即使这是一个已经被新连接替代的旧会话，也要清理由它发出/
                // 收到的邀请；但绝不能误删同一角色新会话刚登记的邀请。
                var staleInvites = new List<ushort>();
                foreach (var kv in _pendingInvites)
                {
                    var invite = kv.Value;
                    var disconnectedInvitee =
                        kv.Key == userId &&
                        invite.InviteeSessionId == sessionId;
                    var disconnectedInviter =
                        invite.InviterUserId == userId &&
                        invite.InviterSessionId == sessionId;
                    if (disconnectedInvitee || disconnectedInviter)
                        staleInvites.Add(kv.Key);
                }
                foreach (var inviteeUserId in staleInvites)
                    _pendingInvites.Remove(inviteeUserId);

                if (_userToParty.TryGetValue(userId, out var partyId) &&
                    _parties.TryGetValue(partyId, out var party))
                {
                    var member = party.GetMember(userId);
                    if (member == null || member.SessionId != sessionId)
                        return PartyOpResult.Fail("stale_session");
                }

                return LeaveLocked(
                           userId,
                           preservePartyOnLeaderExit: true) ??
                       PartyOpResult.Fail("not_in_party");
            }
        }

        private bool TryGetMemberLocked(
            ushort userId,
            out Party party,
            out PartyMember member)
        {
            party = null;
            member = null;
            if (!_userToParty.TryGetValue(userId, out var partyId) ||
                !_parties.TryGetValue(partyId, out party))
            {
                return false;
            }

            member = party.GetMember(userId);
            return member != null;
        }

        private static PartyMember CloneMember(PartyMember member)
        {
            return new PartyMember
            {
                UserId = member.UserId,
                CharacterId = member.CharacterId,
                SessionId = member.SessionId,
                Name = member.Name,
                Level = member.Level,
                Job = member.Job,
                IpBytes = member.IpBytes == null
                    ? null
                    : (byte[])member.IpBytes.Clone(),
                P2pPort = member.P2pPort,
                AccId = member.AccId,
            };
        }

        /// <summary>
        /// 登记一条与双方当前会话和队伍代际绑定的 type0 请求。
        /// 双方都无队是普通建队；仅一方有队时，另一方加入该现有队伍。
        /// </summary>
        public bool RecordInvite(
            ushort inviteeUserId,
            System.Guid inviteeSessionId,
            ushort inviterUserId,
            System.Guid inviterSessionId,
            out string failureReason)
        {
            failureReason = null;
            if (inviteeUserId == 0 ||
                inviterUserId == 0 ||
                inviteeUserId == inviterUserId ||
                inviteeSessionId == System.Guid.Empty ||
                inviterSessionId == System.Guid.Empty)
            {
                failureReason = "invalid_invite";
                return false;
            }

            lock (_lock)
            {
                var inviterInParty = TryGetMemberLocked(
                    inviterUserId,
                    out var inviterParty,
                    out var inviterMember);
                var inviteeInParty = TryGetMemberLocked(
                    inviteeUserId,
                    out var inviteeParty,
                    out var inviteeMember);
                if (inviterInParty &&
                    inviterMember.SessionId != inviterSessionId)
                {
                    failureReason = "stale_session";
                    return false;
                }
                if (inviteeInParty &&
                    inviteeMember.SessionId != inviteeSessionId)
                {
                    failureReason = "stale_session";
                    return false;
                }
                if (inviterInParty && inviteeInParty)
                {
                    failureReason = "both_in_party";
                    return false;
                }
                if (inviterInParty)
                {
                    if (inviterParty.LeaderUserId != inviterUserId)
                    {
                        failureReason = "not_leader";
                        return false;
                    }
                    if (inviterParty.IsFull)
                    {
                        failureReason = "party_full";
                        return false;
                    }
                }
                if (inviteeInParty && inviteeParty.IsFull)
                {
                    failureReason = "party_full";
                    return false;
                }

                _pendingInvites[inviteeUserId] =
                    new PendingPartyInvite
                    {
                        InviteeSessionId = inviteeSessionId,
                        InviterUserId = inviterUserId,
                        InviterSessionId = inviterSessionId,
                        InviterPartyId = inviterParty?.PartyId ?? 0,
                        InviteePartyId = inviteeParty?.PartyId ?? 0,
                    };
                return true;
            }
        }

        /// <summary>
        /// 在同一 PartyManager 锁内匹配 pending、复验双方会话与 PartyId，
        /// 再完成普通建队、邀请入队或申请加入现有队伍。
        /// </summary>
        public PartyOpResult AcceptInvite(
            ushort inviteeUserId,
            System.Guid inviteeSessionId,
            ushort inviterUserId,
            System.Guid inviterSessionId,
            PartyMember inviterMember,
            PartyMember inviteeMember,
            out string mode)
        {
            mode = null;
            if (inviterMember == null ||
                inviteeMember == null ||
                inviterMember.UserId != inviterUserId ||
                inviteeMember.UserId != inviteeUserId ||
                inviterMember.SessionId != inviterSessionId ||
                inviteeMember.SessionId != inviteeSessionId)
            {
                return PartyOpResult.Fail("invalid_member_identity");
            }

            lock (_lock)
            {
                if (!_pendingInvites.TryGetValue(
                        inviteeUserId, out var invite) ||
                    invite.InviteeSessionId != inviteeSessionId ||
                    invite.InviterUserId != inviterUserId ||
                    invite.InviterSessionId != inviterSessionId)
                {
                    return PartyOpResult.Fail("invite_not_found_or_stale");
                }

                // An exact response consumes this request once. State or
                // capacity failures below require a fresh REQUEST_PEER and
                // cannot become valid again through a same-session ABA.
                _pendingInvites.Remove(inviteeUserId);

                var inviterInParty = TryGetMemberLocked(
                    inviterUserId,
                    out var inviterParty,
                    out var currentInviter);
                var inviteeInParty = TryGetMemberLocked(
                    inviteeUserId,
                    out var inviteeParty,
                    out var currentInvitee);
                if (invite.InviterPartyId == 0)
                {
                    if (inviterInParty)
                        return PartyOpResult.Fail("inviter_party_changed");
                }
                else if (!inviterInParty ||
                         inviterParty.PartyId != invite.InviterPartyId ||
                         currentInviter.SessionId != inviterSessionId ||
                         inviterParty.LeaderUserId != inviterUserId)
                {
                    return PartyOpResult.Fail("inviter_not_current_leader");
                }

                if (invite.InviteePartyId == 0)
                {
                    if (inviteeInParty)
                        return PartyOpResult.Fail("invitee_party_changed");
                }
                else if (!inviteeInParty ||
                         inviteeParty.PartyId != invite.InviteePartyId ||
                         currentInvitee.SessionId != inviteeSessionId)
                {
                    return PartyOpResult.Fail("invitee_party_changed");
                }

                var destination = invite.InviterPartyId != 0
                    ? inviterParty
                    : inviteeParty;
                if (destination?.IsFull == true)
                    return PartyOpResult.Fail("party_full");

                if (invite.InviterPartyId == 0 &&
                    invite.InviteePartyId == 0)
                {
                    mode = "create-inviter-party";
                    var created = CreateParty(inviterMember);
                    if (!created.Ok)
                        return created;
                    var joined = Join(created.Party.PartyId, inviteeMember);
                    if (!joined.Ok)
                        Disband(created.Party.PartyId);
                    return joined;
                }
                if (invite.InviterPartyId != 0)
                {
                    mode = "invite-into-inviter-party";
                    return Join(inviterParty.PartyId, inviteeMember);
                }

                mode = "apply-into-invitee-party";
                return Join(inviteeParty.PartyId, inviterMember);
            }
        }

        public bool CancelInvite(
            ushort inviteeUserId,
            System.Guid inviteeSessionId,
            ushort inviterUserId,
            System.Guid inviterSessionId)
        {
            lock (_lock)
            {
                if (!_pendingInvites.TryGetValue(
                        inviteeUserId, out var invite) ||
                    invite.InviteeSessionId != inviteeSessionId ||
                    invite.InviterUserId != inviterUserId ||
                    invite.InviterSessionId != inviterSessionId)
                {
                    return false;
                }

                _pendingInvites.Remove(inviteeUserId);
                return true;
            }
        }

        private sealed class PendingPartyInvite
        {
            internal System.Guid InviteeSessionId;
            internal ushort InviterUserId;
            internal System.Guid InviterSessionId;
            internal int InviterPartyId;
            internal int InviteePartyId;
        }

        public int PartyCount
        {
            get { lock (_lock) { return _parties.Count; } }
        }
    }
}
