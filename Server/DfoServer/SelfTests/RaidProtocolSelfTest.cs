using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DfoServer.Game.Raid;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class RaidProtocolSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var eventTemplate = new List<SelectCharacterPacketTemplate>
            {
                new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.Raw,
                    Command = 0x00,
                    Type = 0x006C,
                    OccurrenceIndex = 0,
                },
            };
            var eventDataSource = new FixedSelectCharacterDataSource();
            var firstEventBody = SelectCharacterPacketBuilder.BuildPacketStream(
                    eventDataSource,
                    0,
                    0,
                    eventTemplate)
                .First()
                .Skip(15)
                .ToArray();
            var secondEventBody = SelectCharacterPacketBuilder.BuildPacketStream(
                    eventDataSource,
                    0,
                    0,
                    eventTemplate)
                .First()
                .Skip(15)
                .ToArray();
            Check("character init exposes the active RAID event",
                firstEventBody.Length == 17
                && secondEventBody.SequenceEqual(firstEventBody)
                && BitConverter.ToUInt16(firstEventBody, 0) == 1
                && BitConverter.ToUInt16(firstEventBody, 2) == 0x00B5
                && firstEventBody.Skip(4).All(value => value == 0),
                ref failures);

            Check("Anton dungeon set is recognized",
                RaidHandler.IsAntonRaidDungeon(210)
                && RaidHandler.IsAntonRaidDungeon(224)
                && !RaidHandler.IsAntonRaidDungeon(217),
                ref failures);

            var passiveObjectStartMap = DungeonNotificationBuilder.BuildStartMap(
                new Dungeon.MazeSumInfo
                {
                    Index = 1,
                    X = 0,
                    Y = 0,
                    Monsters = new List<Dungeon.MonsterSumInfo>(),
                },
                firstMonsterSequence: 0,
                ridableEntries: new[]
                {
                    new DfoServer.Game.Dungeon.RidableObjectSpawnEntry
                    {
                        ObjectIndex = 18865,
                        PosX = 100,
                        PosY = 200,
                        Faction = 0,
                        SpawnMode = 1,
                    },
                });
            Check("START_MAP writes randomized object spawn mode at record +16",
                passiveObjectStartMap.Length == 42
                && BitConverter.ToInt32(passiveObjectStartMap, 37) == 1,
                ref failures);
            var raidClock = 0L;
            var raidManager = new RaidManager(() => raidClock);
            var timeoutLeader = new RaidMember
            {
                UserId = 7,
                CharacterId = 7007,
                SessionId = Guid.NewGuid(),
            };
            var timeoutRaid = raidManager.Create(Array.Empty<byte>(), timeoutLeader);
            Check("raid attack timeout is an atomic terminal transition",
                raidManager.TryBeginStart(timeoutLeader.UserId, out _)
                && raidManager.TryCompleteStart(timeoutRaid.RaidId, timeoutLeader.UserId, out _)
                && AdvanceRaidClockAndFail(),
                ref failures);

            bool AdvanceRaidClockAndFail()
            {
                raidClock = 2_400_000;
                return raidManager.TryFailPhase(timeoutRaid.RaidId, 0, out var failedRaid)
                    && failedRaid.State == 4
                    && failedRaid.StateArgument == 1
                    && failedRaid.PhaseIndex == 0
                    && failedRaid.PhaseClearTimeSeconds == 2400
                    && !raidManager.TryFailPhase(timeoutRaid.RaidId, 0, out _);
            }

            var phaseTwoClock = 0L;
            var phaseTwoManager = new RaidManager(() => phaseTwoClock);
            var phaseTwoLeader = new RaidMember
            {
                UserId = 8,
                CharacterId = 8008,
                SessionId = Guid.NewGuid(),
            };
            var phaseTwoRaid = phaseTwoManager.Create(Array.Empty<byte>(), phaseTwoLeader);
            Check("Anton phase two timeout uses the failure terminal argument",
                phaseTwoManager.TryBeginStart(phaseTwoLeader.UserId, out _)
                && phaseTwoManager.TryCompleteStart(phaseTwoRaid.RaidId, phaseTwoLeader.UserId, out _)
                && phaseTwoManager.TryEnterPhaseBreak(phaseTwoRaid.RaidId, out _)
                && phaseTwoManager.TryCompletePhase(phaseTwoRaid.RaidId, out var phaseBreak)
                && phaseBreak.State == 5
                && phaseTwoManager.TryPrepareNextPhase(phaseTwoLeader.UserId, out _)
                && phaseTwoManager.TryCompletePreparedNextPhase(phaseTwoRaid.RaidId, out var phaseTwoStarted)
                && phaseTwoStarted.PhaseIndex == 1
                && FailPhaseTwo(),
                ref failures);

            bool FailPhaseTwo()
            {
                phaseTwoClock = 2_400_000;
                return phaseTwoManager.TryFailPhase(phaseTwoRaid.RaidId, 1, out var failedRaid)
                    && failedRaid.State == 4
                    && failedRaid.StateArgument == 1
                    && failedRaid.PhaseIndex == 1
                    && failedRaid.PhaseClearTimeSeconds == 2400
                    && !phaseTwoManager.TryFailPhase(phaseTwoRaid.RaidId, 1, out _);
            }
            Check("raid dungeon selection requires an active raid",
                DungeonEntryHandler.IsRaidDungeonSelectionAllowed(
                    GameNetworkConfig.NormalGamePort,
                    raid: null)
                && !DungeonEntryHandler.IsRaidDungeonSelectionAllowed(
                    GameNetworkConfig.RaidGamePort,
                    raid: null)
                && !DungeonEntryHandler.IsRaidDungeonSelectionAllowed(
                    GameNetworkConfig.RaidGamePort,
                    new RaidSnapshot { State = 0 })
                && DungeonEntryHandler.IsRaidDungeonSelectionAllowed(
                    GameNetworkConfig.RaidGamePort,
                    new RaidSnapshot { State = 2 }),
                ref failures);

            var member = new RaidMemberSnapshot
            {
                UserId = 1002,
                CharacterId = 1002,
                NameBytes = Encoding.UTF8.GetBytes("2001"),
                Job = 3,
                GrowType = 0x21,
                PartyIndex = 3,
            };
            var title = Encoding.UTF8.GetBytes("Raid: 2001");
            var ack = RaidPacketBuilder.BuildCreateAck(1002);
            Check("CREATE_RAID ACK contains success and raid key",
                ack.Length == 5 && ack[0] == 1
                && BitConverter.ToUInt32(ack, 1) == 1002,
                ref failures);

            var waiting = RaidPacketBuilder.BuildWaitingList(member);
            Check("RAID_WAITING_LIST carries member party index",
                waiting.Length == 10
                && BitConverter.ToUInt32(waiting, 0) == 1
                && BitConverter.ToUInt16(waiting, 4) == 1002
                && BitConverter.ToUInt32(waiting, 6) == 3,
                ref failures);

            var modify = RaidPacketBuilder.BuildRaidModify(
                1002, title, 0, 0, member, new[] { member });
            Check("RAID_MODIFY carries title and member",
                modify.Length > title.Length
                && BitConverter.ToUInt32(modify, 0) == 1002
                && BitConverter.ToUInt32(modify, 8) == 1002,
                ref failures);

            var membersUpdate = RaidPacketBuilder.BuildRaidMembersUpdate(
                1002, new[] { member });
            Check("RAID member carries base job and full awakening type",
                membersUpdate.Length > 27
                && BitConverter.ToUInt32(membersUpdate, 23) == 3
                && membersUpdate[27] == 0x21,
                ref failures);

            var costs = RaidPacketBuilder.BuildEntryCostInfo(new[]
            {
                new RaidEntryCostStatus { UserId = 1002, Ready = true, OwnedCount = 3 },
            });
            Check("RAID_ENTRY_COST_INFO carries readiness",
                costs.Length >= 14
                && BitConverter.ToUInt32(costs, 0) == 1
                && BitConverter.ToUInt16(costs, 4) == 1002,
                ref failures);

            try
            {
                Check("Anton randomized objects use PVF template categories",
                    DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(18865) == 1
                    && DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(58530) == 0,
                    ref failures);

                var energyMaze = Dungeon.GetDungeonDefaultMaze(218);
                var energyRooms = Dungeon.GetDungeonRoomCoordinates(218, 0, energyMaze);
                foreach (var room in energyRooms)
                {
                    var map = DungeonMapCatalog.GetMapFile(room.MapId);
                    Console.WriteLine($"[Anton218] room=({room.X},{room.Y}) map={room.MapId} path={room.FilePath} passive={map.PassiveObjects.Count} special={map.SpecialPassiveObjects.Count} objects=" +
                        string.Join(",", map.SpecialPassiveObjects.Select(obj => obj.ObjectCode)) + " passiveCodes=" + string.Join(",", map.PassiveObjects.Select(obj => obj.ObjectCode)));
                }                var buffs = AntonRaidRewardProvider.GetRaidBuffDefinitions();
                var monsters = AntonRaidRewardProvider.GetRaidMonsterDefinitions();
                var expectedBuffs = new[]
                {
                    "ATTACK BONUS",
                    "INVINCIBLE",
                    "RESTORE",
                    "INCREASE TIME",
                    "INCREASE COIN",
                };
                Check("Anton PVF exposes all five raid buffs",
                    expectedBuffs.All(expected =>
                        buffs.Any(buff => string.Equals(
                            buff.TypeName,
                            expected,
                            StringComparison.OrdinalIgnoreCase))),
                    ref failures);
                Check("Anton PVF exposes situation monsters",
                    monsters.Count >= 11
                    && monsters.Any(entry => entry.DungeonId == 210)
                    && monsters.Any(entry => entry.DungeonId == 219),
                    ref failures);
                Check("Anton PVF exposes positive phase timing",
                    AntonRaidRewardProvider.GetStartDelaySeconds() > 0
                    && AntonRaidRewardProvider.GetPhaseBreakSeconds() > 0,
                    ref failures);
            }
            catch (System.IO.FileNotFoundException ex)
            {
                Console.WriteLine($"[SKIP] Anton PVF validation: {ex.Message}");
            }
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "OK" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private sealed class FixedSelectCharacterDataSource : ISelectCharacterDataSource
        {
            public SelectCharacterDataSnapshot Load(int characterId, int accountId)
                => new SelectCharacterDataSnapshot();

            public int GetSeedCharacterId() => 0;

            public void InitializeNewCharacter(int characterId, int accountId, byte job)
            {
            }
        }
    }
}
