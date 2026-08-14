using System;
using DfoServer.Game.Party;

namespace DfoServer.SelfTests
{
    // 组队状态核心(格式无关)自测: 验证 PartyManager 生命周期的不变量 ——
    // 建队 / 满员拒绝 / 重复加入拒绝 / 离队转队长(含slot0修正) / 踢人权限 / 解散 / 索引一致 /
    // 槽位分配与复用 / 换队时原队离队结果外带(PriorPartyLeave) / 待应答邀请状态机。
    // 注: 这里只测服务端状态机逻辑; 组队封包的字节布局(86jp)需另行对照真机确认。
    public static class PartySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PARTY selftest ===");
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) { pass++; Console.WriteLine($"  [PASS] {name}"); }
                else { fail++; Console.WriteLine($"  [FAIL] {name}"); }
            }

            PartyMember M(ushort uid, string name, byte lvl = 1) =>
                new PartyMember { UserId = uid, CharacterId = uid, Name = name, Level = lvl, Job = 0, SessionId = Guid.NewGuid() };

            // ---- 建队 ----
            var mgr = new PartyManager();
            var created = mgr.CreateParty(M(1002, "leader", 86));
            var party = created.Party;
            Check("create: ok, no prior party", created.Ok && created.PriorPartyLeave == null);
            Check("create: count==1", party.Count == 1);
            Check("create: leader is 1002", party.LeaderUserId == 1002);
            Check("create: IsLeader(1002)", party.IsLeader(1002));
            Check("create: GetPartyByUser(1002)==party", mgr.GetPartyByUser(1002) == party);
            Check("create: slot0", party.GetMember(1002).SlotIndex == 0);

            // ---- 加入至满员 ----
            var r2 = mgr.Join(party.PartyId, M(1003, "m2", 14));
            var r3 = mgr.Join(party.PartyId, M(1004, "m3"));
            var r4 = mgr.Join(party.PartyId, M(1005, "m4"));
            Check("join m2/m3/m4 all ok", r2.Ok && r3.Ok && r4.Ok);
            Check("count==4 full", party.Count == 4 && party.IsFull);
            Check("slots 0..3 distinct",
                party.GetMember(1002).SlotIndex == 0 && party.GetMember(1003).SlotIndex == 1 &&
                party.GetMember(1004).SlotIndex == 2 && party.GetMember(1005).SlotIndex == 3);

            // 第 5 人拒绝
            var r5 = mgr.Join(party.PartyId, M(1006, "m5"));
            Check("5th join rejected (party_full)", !r5.Ok && r5.Reason == "party_full");
            Check("1006 not indexed", mgr.GetPartyByUser(1006) == null);

            // 重复加入拒绝
            var rdup = mgr.Join(party.PartyId, M(1003, "m2dup"));
            Check("duplicate join rejected (already_member)", !rdup.Ok && rdup.Reason == "already_member");

            // ---- 踢人权限 ----
            var kickByNonLeader = mgr.Kick(1003, 1004);
            Check("non-leader kick rejected (not_leader)", !kickByNonLeader.Ok && kickByNonLeader.Reason == "not_leader");
            var kickSelf = mgr.Kick(1002, 1002);
            Check("leader kick self rejected", !kickSelf.Ok && kickSelf.Reason == "cannot_kick_self");
            var kickOk = mgr.Kick(1002, 1005);
            Check("leader kick 1005 ok", kickOk.Ok && kickOk.TargetUserId == 1005);
            Check("after kick count==3, 1005 gone", party.Count == 3 && !party.Contains(1005) && mgr.GetPartyByUser(1005) == null);

            // 槽位复用: 1005 曾占 slot3, 新加入者应复用 slot3
            var rReuse = mgr.Join(party.PartyId, M(1007, "reuse"));
            Check("join reuses freed slot3", rReuse.Ok && party.GetMember(1007).SlotIndex == 3);

            // ---- 非队长离队: 队长不变 ----
            var leaveNonLeader = mgr.Leave(1004);
            Check("non-leader leave ok, leader unchanged", leaveNonLeader.Ok && !leaveNonLeader.LeaderChanged && party.LeaderUserId == 1002);
            Check("1004 removed from index", mgr.GetPartyByUser(1004) == null);

            // ---- 队长离队: 转移给下一名成员(按槽位), 且新队长必须换到 slot0(客户端以 slot0=队长判定) ----
            // 现有: 1002(slot0,leader) 1003(slot1) 1007(slot3)
            int retiredPartyId = party.PartyId;
            var leaveLeader = mgr.Leave(1002);
            var successorParty = leaveLeader.Party;
            Check("leader leave -> LeaderChanged", leaveLeader.Ok && leaveLeader.LeaderChanged);
            Check("leader leave retires the old party generation",
                leaveLeader.RetiredParty != null
                && leaveLeader.RetiredParty.PartyId == retiredPartyId
                && successorParty.PartyId != retiredPartyId
                && mgr.GetPartyById(retiredPartyId) == null);
            Check("new leader is slot-min survivor 1003",
                leaveLeader.NewLeaderUserId == 1003
                && successorParty.LeaderUserId == 1003);
            Check("new leader promoted to slot0",
                successorParty.GetMember(1003).SlotIndex == 0);
            Check("1002 removed",
                mgr.GetPartyByUser(1002) == null
                && !successorParty.Contains(1002));

            // ---- 未通关副本中队长主动放弃: 队伍不解散, 顺位成员接任并继续留本 ----
            var dungeonMgr = new PartyManager();
            var dungeonParty = dungeonMgr.CreateParty(M(1101, "dungeon-leader")).Party;
            dungeonMgr.Join(dungeonParty.PartyId, M(1102, "dungeon-b"));
            var dungeonLeaderGiveup = dungeonMgr.Leave(1101);
            var dungeonSuccessorParty = dungeonLeaderGiveup.Party;
            Check("dungeon leader giveup keeps party",
                dungeonLeaderGiveup.Ok
                && !dungeonLeaderGiveup.Disbanded
                && dungeonSuccessorParty.Count == 1);
            Check("dungeon leader giveup promotes B",
                dungeonLeaderGiveup.LeaderChanged
                && dungeonLeaderGiveup.NewLeaderUserId == 1102
                && dungeonSuccessorParty.LeaderUserId == 1102);
            Check("dungeon promoted B occupies slot0",
                dungeonSuccessorParty.GetMember(1102)?.SlotIndex == 0);
            Check("dungeon former leader no longer indexed",
                dungeonMgr.GetPartyByUser(1101) == null
                && dungeonMgr.GetPartyByUser(1102)
                    == dungeonSuccessorParty);

            // 通关回城不调用 Leave: 服务端队伍应保持原样，由 TownHandler 只做整队回城 fan-out。
            var completedMgr = new PartyManager();
            var completedParty = completedMgr.CreateParty(M(1201, "completed-leader")).Party;
            completedMgr.Join(completedParty.PartyId, M(1202, "completed-b"));
            Check("completed dungeon return preserves party", completedParty.Count == 2
                && completedParty.LeaderUserId == 1201
                && completedMgr.GetPartyByUser(1201) == completedParty
                && completedMgr.GetPartyByUser(1202) == completedParty);

            // ---- 逐个离队至解散 ----
            mgr.Leave(1007);
            var last = mgr.Leave(1003);
            Check("last leave disbands party", last.Ok && last.Disbanded);
            Check("disbanded: GetPartyByUser(1003)==null", mgr.GetPartyByUser(1003) == null);
            Check("disbanded: GetPartyById==null", mgr.GetPartyById(party.PartyId) == null);
            Check("manager party count==0", mgr.PartyCount == 0);

            // ---- 显式解散状态层: 清除整队索引并保留解散前快照供 Handler 通知 ----
            var explicitDisbandManager = new PartyManager();
            var explicitDisbandParty =
                explicitDisbandManager.CreateParty(
                    M(1501, "explicit-leader")).Party;
            explicitDisbandManager.Join(
                explicitDisbandParty.PartyId,
                M(1502, "explicit-member-a"));
            explicitDisbandManager.Join(
                explicitDisbandParty.PartyId,
                M(1503, "explicit-member-b"));
            var explicitDisband = explicitDisbandManager.Disband(
                explicitDisbandParty.PartyId);
            Check("explicit disband returns the full member snapshot",
                explicitDisband.Ok &&
                explicitDisband.Disbanded &&
                explicitDisband.RemainingMembers.Count == 3);
            Check("explicit disband clears every user index",
                explicitDisbandManager.GetPartyByUser(1501) == null &&
                explicitDisbandManager.GetPartyByUser(1502) == null &&
                explicitDisbandManager.GetPartyByUser(1503) == null);
            Check("explicit disband removes party index",
                explicitDisbandManager.GetPartyById(
                    explicitDisbandParty.PartyId) == null &&
                explicitDisbandManager.PartyCount == 0);

            // ---- TransferLeader 显式转移 + 非成员拒绝 ----
            var p2 = mgr.CreateParty(M(2001, "L")).Party;
            mgr.Join(p2.PartyId, M(2002, "A"));
            var trBad = mgr.TransferLeader(2001, 9999);
            Check("transfer to non-member rejected", !trBad.Ok && trBad.Reason == "target_not_member");
            var trByNon = mgr.TransferLeader(2002, 2001);
            Check("transfer by non-leader rejected", !trByNon.Ok && trByNon.Reason == "not_leader");
            var trOk = mgr.TransferLeader(2001, 2002);
            Check("transfer leader ok", trOk.Ok && p2.LeaderUserId == 2002);
            Check("transferred leader moved to slot0", p2.GetMember(2002).SlotIndex == 0);

            // ---- 断线清理 ----
            var disconnectedSessionId = p2.GetMember(2001).SessionId;
            var staleDisc = mgr.OnSessionDisconnected(2001, Guid.NewGuid());
            Check(
                "stale disconnect generation cannot remove current member",
                !staleDisc.Ok && staleDisc.Reason == "stale_session"
                && p2.Contains(2001));
            var disc = mgr.OnSessionDisconnected(
                2001, disconnectedSessionId);
            Check("disconnect removes member", disc.Ok && mgr.GetPartyByUser(2001) == null && p2.Contains(2001) == false);

            // ---- 换队: 加入新队伍时从旧队伍移除, 且原队离队结果经 PriorPartyLeave 带出供通知 ----
            var pa = mgr.CreateParty(M(3001, "pa-leader")).Party;
            var pb = mgr.CreateParty(M(3002, "pb-leader")).Party;
            mgr.Join(pa.PartyId, M(3003, "mover"));
            Check("mover in pa", mgr.GetPartyByUser(3003) == pa && pa.Contains(3003));
            var moved = mgr.Join(pb.PartyId, M(3003, "mover"));
            Check("mover switched to pb", moved.Ok && mgr.GetPartyByUser(3003) == pb && pb.Contains(3003) && !pa.Contains(3003));
            Check("prior party leave surfaced for notification",
                moved.PriorPartyLeave != null
                && moved.PriorPartyLeave.Party == pa
                && moved.PriorPartyLeave.RemainingMembers.Count == 1
                && moved.PriorPartyLeave.RemainingMembers[0].UserId == 3001);

            // 原队队长换队: 原队须正确转移队长并换 slot0(静默离队与显式离队共用同一实现)
            var pc = mgr.CreateParty(M(3101, "pc-leader")).Party;
            mgr.Join(pc.PartyId, M(3102, "pc-m2"));
            var leaderMoved = mgr.CreateParty(M(3101, "pc-leader"));
            var priorSuccessor =
                leaderMoved.PriorPartyLeave?.Party;
            Check("prior-party leader auto-leave transfers leadership",
                leaderMoved.PriorPartyLeave != null
                && leaderMoved.PriorPartyLeave.LeaderChanged
                && leaderMoved.PriorPartyLeave.NewLeaderUserId == 3102
                && leaderMoved.PriorPartyLeave.RetiredParty?.PartyId
                    == pc.PartyId
                && priorSuccessor != null
                && priorSuccessor.PartyId != pc.PartyId
                && priorSuccessor.LeaderUserId == 3102
                && priorSuccessor.GetMember(3102).SlotIndex == 0);

            // ---- 会话代际保护: 旧连接不能修改新连接拥有的队伍状态 ----
            var guarded = new PartyManager();
            var guardedLeader = M(3201, "guarded-leader");
            var guardedTarget = M(3202, "guarded-target");
            var guardedParty =
                guarded.CreateParty(guardedLeader).Party;
            guarded.Join(guardedParty.PartyId, guardedTarget);
            guardedParty.TitleBytes = new byte[] { 0x41, 0x42 };

            var staleLeave = guarded.Leave(
                guardedLeader.UserId, Guid.NewGuid());
            Check("guarded leave rejects stale session",
                !staleLeave.Ok
                && staleLeave.Reason == "stale_session"
                && guardedParty.Contains(guardedLeader.UserId));

            var staleKickActor = guarded.Kick(
                guardedLeader.UserId,
                Guid.NewGuid(),
                guardedTarget.UserId,
                guardedTarget.SessionId);
            var staleKickTarget = guarded.Kick(
                guardedLeader.UserId,
                guardedLeader.SessionId,
                guardedTarget.UserId,
                Guid.NewGuid());
            Check("guarded kick rejects stale actor and target sessions",
                !staleKickActor.Ok
                && !staleKickTarget.Ok
                && guardedParty.Count == 2);

            var staleRebuild = guarded.RebuildWithLeader(
                guardedParty.PartyId,
                guardedLeader.UserId,
                guardedLeader.SessionId,
                guardedTarget.UserId,
                Guid.NewGuid());
            Check("stale leader rebuild is atomic",
                !staleRebuild.Ok
                && guarded.GetPartyById(guardedParty.PartyId)
                    == guardedParty
                && guarded.GetPartyByUser(guardedLeader.UserId)
                    == guardedParty
                && guarded.GetPartyByUser(guardedTarget.UserId)
                    == guardedParty);

            var rebuilt = guarded.RebuildWithLeader(
                guardedParty.PartyId,
                guardedLeader.UserId,
                guardedLeader.SessionId,
                guardedTarget.UserId,
                guardedTarget.SessionId);
            Check("guarded leader rebuild commits one new generation",
                rebuilt.Ok
                && guarded.GetPartyById(guardedParty.PartyId) == null
                && rebuilt.Party.LeaderUserId
                    == guardedTarget.UserId
                && rebuilt.Party.GetMember(guardedTarget.UserId)
                    .SlotIndex == 0
                && rebuilt.Party.GetMember(guardedLeader.UserId)
                    .SessionId == guardedLeader.SessionId
                && rebuilt.Party.TitleBytes.Length == 2);

            // ---- 待应答邀请登记/消费(REQUEST_MEMBER_ENTER → MEMBER_ENTER_REPLY 的状态机)----
            var inv = new PartyManager();
            var host = inv.CreateParty(M(4001, "host")).Party;
            var hostSessionId = host.GetMember(4001).SessionId;
            var invitedGuest = M(4002, "guest");
            inv.RecordInvite(
                4002,
                invitedGuest.SessionId,
                4001,
                hostSessionId,
                host.PartyId);       // A(4001) 邀请 B(4002)
            Check(
                "consume unrelated invite fails",
                !inv.TryConsumeInvite(
                    9998,
                    Guid.NewGuid(),
                    4001,
                    hostSessionId,
                    out _));
            var got = inv.TryConsumeInvite(
                4002,
                invitedGuest.SessionId,
                4001,
                hostSessionId,
                out var invPid);
            Check("consume invite ok", got && invPid == host.PartyId);
            Check(
                "invite consumed once (2nd fails)",
                !inv.TryConsumeInvite(
                    4002,
                    invitedGuest.SessionId,
                    4001,
                    hostSessionId,
                    out _));
            var joinB = inv.Join(invPid, invitedGuest);   // B 接受 → 入队
            Check("invited guest joins host party", joinB.Ok && host.Count == 2 && host.Contains(4002));

            // 生产协议邀请必须绑定双方会话。错误代际的应答既不能消费，
            // 也不能使随后来自正确会话的应答失效。
            var exactInvites = new PartyManager();
            var inviterSession = Guid.NewGuid();
            var inviteeSession = Guid.NewGuid();
            Check(
                "session-bound invite records",
                exactInvites.RecordInvite(
                    4102,
                    inviteeSession,
                    4101,
                    inviterSession,
                    77));
            Check(
                "stale inviter session cannot consume invite",
                !exactInvites.TryConsumeInvite(
                    4102,
                    inviteeSession,
                    4101,
                    Guid.NewGuid(),
                    out _));
            Check(
                "stale invitee session cannot consume invite",
                !exactInvites.TryConsumeInvite(
                    4102,
                    Guid.NewGuid(),
                    4101,
                    inviterSession,
                    out _));
            Check(
                "matching sessions consume invite once",
                exactInvites.TryConsumeInvite(
                    4102,
                    inviteeSession,
                    4101,
                    inviterSession,
                    out var exactPartyId) &&
                exactPartyId == 77 &&
                !exactInvites.TryConsumeInvite(
                    4102,
                    inviteeSession,
                    4101,
                    inviterSession,
                    out _));

            var newInviterSession = Guid.NewGuid();
            var newInviteeSession = Guid.NewGuid();
            exactInvites.RecordInvite(
                4104,
                newInviteeSession,
                4103,
                newInviterSession,
                88);
            exactInvites.OnSessionDisconnected(
                4103, Guid.NewGuid());
            Check(
                "old disconnect cannot clear new-session invite",
                exactInvites.TryConsumeInvite(
                    4104,
                    newInviteeSession,
                    4103,
                    newInviterSession,
                    out var retainedPartyId) &&
                retainedPartyId == 88);

            var disconnectedInviteeSession = Guid.NewGuid();
            exactInvites.RecordInvite(
                4106,
                disconnectedInviteeSession,
                4105,
                newInviterSession,
                99);
            exactInvites.OnSessionDisconnected(
                4105, newInviterSession);
            Check(
                "matching inviter disconnect clears exact invite",
                !exactInvites.TryConsumeInvite(
                    4106,
                    disconnectedInviteeSession,
                    4105,
                    newInviterSession,
                    out _));

            // 邀请者断线 → 其发出的待应答邀请被清掉(被邀请者再应答无效)
            var thirdInviteeSession = Guid.NewGuid();
            inv.RecordInvite(
                4003,
                thirdInviteeSession,
                4001,
                hostSessionId,
                host.PartyId);
            inv.OnSessionDisconnected(
                4001, hostSessionId);         // 邀请者(队长)断线
            Check(
                "inviter disconnect clears pending invite",
                !inv.TryConsumeInvite(
                    4003,
                    thirdInviteeSession,
                    4001,
                    hostSessionId,
                    out _));

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }
    }
}
