using System;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Dungeon;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class PartyDeathMapMoveSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PARTY_DEATH_MAP_MOVE selftest ===");
            var failures = 0;
            var instance = new DungeonInstance(2, 0);
            var leader = new DungeonRun(instance, 7101, 1, DungeonRunState.Active);
            var follower = new DungeonRun(instance, 7102, 1, DungeonRunState.Active);
            var leaderId = leader.CaptureIdentity();
            var followerId = follower.CaptureIdentity();
            var roster = new[] { leaderId, followerId };
            instance.RegisterParticipantLife(leaderId);
            instance.RegisterParticipantLife(followerId);
            var now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

            instance.MarkParticipantDead(followerId, roster, now, DungeonInstance.PartyWipeDelay);
            Check("living requester can move with a dead follower",
                instance.CanParticipantMoveMap(leaderId, roster), ref failures);
            instance.MarkParticipantAlive(followerId);
            instance.MarkParticipantDead(leaderId, roster, now, DungeonInstance.PartyWipeDelay);
            Check("dead requester can relay a room move while a teammate lives",
                instance.CanParticipantMoveMap(leaderId, roster)
                && instance.IsParticipantDead(leaderId), ref failures);
            Check("a missing or disconnected living teammate cannot authorize a move",
                !instance.CanParticipantMoveMap(leaderId, new[] { leaderId }), ref failures);
            Check("the requester must belong to the connected room roster",
                !instance.CanParticipantMoveMap(leaderId, new[] { followerId }), ref failures);
            var staleFollower = new DungeonRunIdentity(instance.PartyDungeonInstanceId, 7102, 2);
            var otherInstance = new DungeonInstance(2, 0);
            var foreignFollower = new DungeonRunIdentity(otherInstance.PartyDungeonInstanceId, 7103, 1);
            Check("stale and foreign live identities cannot authorize a dead requester",
                !instance.CanParticipantMoveMap(leaderId, new[] { leaderId, staleFollower, foreignFollower }),
                ref failures);
            Check("unknown requester fails closed",
                !instance.CanParticipantMoveMap(staleFollower, roster), ref failures);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var reader = new TcpClient();
                var connect = reader.ConnectAsync(IPAddress.Loopback,
                    ((IPEndPoint)listener.LocalEndpoint).Port);
                using var writer = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                reader.ReceiveTimeout = 1000;
                var session = new EnhancedClientSession(writer, new GamePacketHeader());
                session.Player.CurrentRun = leader;

                Check("handler accepts the recorded dead-requester / living-teammate case",
                    DungeonMapHandler.TryAcceptParticipantMapMoveAsync(session, leader, roster)
                        .GetAwaiter().GetResult()
                    && reader.Available == 0, ref failures);

                var wipe = instance.MarkParticipantDead(followerId, roster, now,
                    DungeonInstance.PartyWipeDelay);
                var accepted = DungeonMapHandler.TryAcceptParticipantMapMoveAsync(session, leader, roster)
                    .GetAwaiter().GetResult();
                // A21 0x00C32C24 sets the pending-move latch. The failure dispatcher
                // at 0x01105E10 clears it via 0x00C2E740; DIE_STATE never does.
                var replied = reader.Client.Poll(100_000, SelectMode.SelectRead);
                var packet = new byte[17];
                if (replied)
                    reader.GetStream().ReadExactly(packet);
                Check("all-dead rejection sends the failure ACK that releases the client move latch",
                    !accepted && replied && packet[0] == 1
                    && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.MOVE_MAP
                    && BitConverter.ToInt32(packet, 3) == packet.Length
                    && packet[15] == 0 && packet[16] == 0, ref failures);
                Check("rejecting a move preserves the pending party wipe",
                    wipe.WipeStarted && instance.IsParticipantDead(leaderId)
                    && instance.IsParticipantDead(followerId), ref failures);

                instance.MarkParticipantAlive(leaderId);
                Check("revive permits a fresh move and invalidates the old wipe deadline",
                    DungeonMapHandler.TryAcceptParticipantMapMoveAsync(session, leader, roster)
                        .GetAwaiter().GetResult()
                    && !instance.TryCommitPartyWipe(wipe.Generation, roster, now.AddMinutes(1)),
                    ref failures);

                session.Player.CurrentRun = new DungeonRun(2, 0);
                Check("an old run cannot send a failure into the replacement run",
                    !DungeonMapHandler.TryAcceptParticipantMapMoveAsync(session, leader, roster)
                        .GetAwaiter().GetResult()
                    && reader.Available == 0, ref failures);
            }
            finally
            {
                listener.Stop();
            }

            var solo = new DungeonRun(2, 0);
            var soloId = solo.CaptureIdentity();
            solo.Instance.RegisterParticipantLife(soloId);
            var soloRoster = new[] { soloId };
            var soloWipe = solo.Instance.MarkParticipantDead(soloId, soloRoster, now,
                DungeonInstance.PartyWipeDelay);
            Check("dead solo player cannot move, including after wipe commit",
                !solo.Instance.CanParticipantMoveMap(soloId, soloRoster)
                && solo.Instance.TryCommitPartyWipe(soloWipe.Generation, soloRoster, now.AddMinutes(1))
                && !solo.Instance.CanParticipantMoveMap(soloId, soloRoster), ref failures);

            Console.WriteLine($"PARTY_DEATH_MAP_MOVE selftest failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
