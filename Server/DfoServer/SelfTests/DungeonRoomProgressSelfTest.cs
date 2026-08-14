using System;
using System.Collections.Generic;
using System.Net.Sockets;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DungeonRoomProgressSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_ROOM_PROGRESS selftest ===");
            var failures = 0;

            using var client = new TcpClient();
            var session = new EnhancedClientSession(client, new GamePacketHeader());
            var run = new Game.Dungeon.DungeonRun(1000, 0);
            session.Player.CurrentRun = run;

            // 房间有 1 个普通怪(blocking) + 1 个 APC(not blocking)
            run.RoomStartSequence = 10;
            run.RoomMonsters = new List<DungeonData.MonsterSumInfo>
            {
                new DungeonData.MonsterSumInfo { Code = 200, Type = 0, Level = 1, IsBlocking = true },
                new DungeonData.MonsterSumInfo { Code = 56408, Type = 8, Level = 25, IsBlocking = false },
            };
            run.RoomKilledSeqIds = new HashSet<ushort>();

            var progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("room with live blocking monster is not passable",
                progress.BlockingCount == 1
                && progress.BlockingRemainingCount == 1
                && !progress.RoomPassable,
                ref failures);

            run.RoomKilledSeqIds.Add(10);
            progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("room passable after killing blocking monster, apc still alive",
                progress.BlockingRemainingCount == 0
                && progress.RemainingCount == 1
                && progress.RoomPassable,
                ref failures);

            // 房间只有 APC(not blocking) — 教程 BOSS 房场景
            run.RoomStartSequence = 20;
            run.RoomMonsters = new List<DungeonData.MonsterSumInfo>
            {
                new DungeonData.MonsterSumInfo { Code = 300, Type = 5, Level = 1, IsBlocking = false },
            };
            run.RoomKilledSeqIds = new HashSet<ushort>();
            progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("room with only non-blocking apc is immediately passable",
                progress.BlockingCount == 0
                && progress.BlockingRemainingCount == 0
                && progress.RoomPassable,
                ref failures);

            // 房间有普通怪(blocking) + APC(not blocking)，普通怪未杀
            run.RoomStartSequence = 30;
            run.RoomMonsters = new List<DungeonData.MonsterSumInfo>
            {
                new DungeonData.MonsterSumInfo { Code = 301, Type = 0, Level = 1, IsBlocking = true },
                new DungeonData.MonsterSumInfo { Code = 302, Type = 5, Level = 1, IsBlocking = false },
            };
            run.RoomKilledSeqIds = new HashSet<ushort>();
            progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("room not passable while blocking normal monster alive",
                progress.BlockingCount == 1
                && progress.BlockingRemainingCount == 1
                && !progress.RoomPassable,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
