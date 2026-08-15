using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;

namespace DfoServer.SelfTests
{
    public static class A21TutorialProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_TUTORIAL_PROTOCOL selftest ===");
            var failures = 0;

            var enterFirst = EnterSelectDungeonStateBuilder
                .BuildA21EnterSelectDungeon(0x0439, initialTutorialLayout: true);
            Check(
                "A21 first tutorial NOTI 27 is 37B with user id at offset 11",
                enterFirst.Length == 37
                && enterFirst[10] == 1
                && BitConverter.ToUInt16(enterFirst, 11) == 0x0439
                && enterFirst[18] == 1,
                ref failures);

            var enterLater = EnterSelectDungeonStateBuilder
                .BuildA21EnterSelectDungeon(0x0439, initialTutorialLayout: false);
            Check(
                "A21 later selection NOTI 27 is 39B with user id at offset 13",
                enterLater.Length == 39
                && enterLater[8] == 1
                && enterLater[12] == 1
                && BitConverter.ToUInt16(enterLater, 13) == 0x0439
                && enterLater[20] == 1,
                ref failures);

            var info = DungeonNotificationBuilder.BuildDungeonInfo(
                10000,
                difficulty: 0,
                bossX: 2,
                bossY: 0,
                hellPartyRoomX: 0xFF,
                hellPartyRoomY: 0xFF);
            Check(
                "A21 DUNGEON_INFO is fixed 32B with boss at offset 6",
                info.Length == 32
                && BitConverter.ToUInt16(info, 0) == 10000
                && info[6] == 2
                && info[7] == 0
                && info[12] == 1
                && info[18] == 0xFF
                && info[19] == 0xFF,
                ref failures);

            var maze = new Dungeon.MazeSumInfo
            {
                X = 0,
                Y = 1,
                Index = 61000,
                Monsters = new List<Dungeon.MonsterSumInfo>
                {
                    new Dungeon.MonsterSumInfo
                    {
                        TemplateOrder = 0,
                        PacketIndex = 1,
                        Code = 61670,
                        Level = 0,
                        Type = 1,
                    },
                    new Dungeon.MonsterSumInfo
                    {
                        TemplateOrder = 0,
                        PacketIndex = 0,
                        Code = 30122489,
                        Level = 0,
                        Type = 0x50,
                        Flag1 = 5,
                    },
                },
            };
            var start = DungeonNotificationBuilder.BuildStartMap(
                maze,
                firstMonsterSequence: 10002,
                randomSeed: 232968,
                hellPartyMode: 2,
                hellPartyFogFlag: 0);
            Check(
                "A21 START_MAP moves actor count to offset 18 and uses 21B actors",
                start.Length == 65
                && BitConverter.ToUInt16(start, 14) == 61000
                && start[7] == 2
                && start[18] == 2
                && start[39] == 0
                && start[61] == 0
                && start[64] == 0xFF,
                ref failures);

            var revisit = DungeonNotificationBuilder.BuildStartMapRevisit(
                maze,
                seed: 232968);
            Check(
                "A21 START_MAP revisit keeps the standard mode marker",
                revisit.Length == 16
                && revisit[7] == 2,
                ref failures);

            var townSnapshot = new TownUserSnapshot
            {
                UserId = 0x0439,
                TownId = 1,
                AreaId = 2,
                PosX = 0x0123,
                PosY = 0x0045,
                Direction = 5,
                State = 0,
            };
            var townPlayer = new PlayerContext
            {
                UserId = townSnapshot.UserId,
                UserState = townSnapshot.State,
            };
            var userState = EnterSelectDungeonStateBuilder.BuildUserState(townPlayer);
            Check(
                "A21 town return starts with USER_STATE body=4B",
                userState.Length == 4
                && userState[0] == 1
                && BitConverter.ToUInt16(userState, 1) == 0x0439
                && userState[3] == 0,
                ref failures);

            var userArea = TownAreaNotificationBuilder.BuildUserArea(townSnapshot);
            Check(
                "A21 USER_AREA is 10B with town/area before coordinates",
                userArea.Length == 10
                && BitConverter.ToUInt16(userArea, 0) == 0x0439
                && userArea[2] == 1
                && userArea[3] == 2
                && BitConverter.ToInt16(userArea, 4) == 0x0123
                && BitConverter.ToInt16(userArea, 6) == 0x0045
                && userArea[8] == 5
                && userArea[9] == 0,
                ref failures);

            var areaUsers = TownAreaNotificationBuilder.BuildAreaUsers(townSnapshot);
            Check(
                "A21 AREA_USERS is 12B with a uint16 count",
                areaUsers.Length == 12
                && areaUsers[0] == 1
                && areaUsers[1] == 2
                && BitConverter.ToUInt16(areaUsers, 2) == 1
                && BitConverter.ToUInt16(areaUsers, 4) == 0x0439
                && BitConverter.ToInt16(areaUsers, 6) == 0x0123
                && BitConverter.ToInt16(areaUsers, 8) == 0x0045
                && areaUsers[10] == 5
                && areaUsers[11] == 0,
                ref failures);

            var userPosition = TownAreaNotificationBuilder.BuildUserPosition(
                townSnapshot,
                motionState: 0x0064);
            Check(
                "A21 USER_POSITION is 9B with a uint16 motion state",
                userPosition.Length == 9
                && BitConverter.ToUInt16(userPosition, 0) == 0x0439
                && BitConverter.ToInt16(userPosition, 2) == 0x0123
                && BitConverter.ToInt16(userPosition, 4) == 0x0045
                && userPosition[6] == 5
                && BitConverter.ToUInt16(userPosition, 7) == 0x0064,
                ref failures);

            var pcRoomResponse = Network.Handlers.TownHandler
                .BuildGetPcRoomTimePointItemResponsePacket();
            Check(
                "A21 town return PC-room response is CMD 0x0279 with a 6B zero body",
                pcRoomResponse.Length == 21
                && pcRoomResponse[0] == 0x01
                && BitConverter.ToUInt16(pcRoomResponse, 1)
                    == (ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM
                && pcRoomResponse[15] == 0
                && pcRoomResponse[20] == 0,
                ref failures);

            var changeBody = new byte[15];
            changeBody[0] = 0;
            changeBody[1] = 0x1E;
            changeBody[5] = 1;
            Check(
                "A21 CHANGE_TUTORIAL_FLAG parses flag at offset 1",
                ChangeTutorialFlagRequest.TryParse(changeBody, out var change)
                && change.Mode == 0
                && change.FlagIndex == 30
                && change.RewardFlag == 1,
                ref failures);

            var compactChangeBody = new byte[]
            {
                0x00, 0x1E, 0x00, 0x00, 0x00, 0x01,
            };
            Check(
                "A21 live CHANGE_TUTORIAL_FLAG accepts compact 6B body",
                ChangeTutorialFlagRequest.TryParse(compactChangeBody, out var compactChange)
                && compactChange.Mode == 0
                && compactChange.FlagIndex == 30
                && compactChange.RewardFlag == 1,
                ref failures);

            Check(
                "A21 CHANGE_TUTORIAL_FLAG rejects body shorter than field prefix",
                !ChangeTutorialFlagRequest.TryParse(new byte[] { 0x00, 0x1E, 0x00, 0x00, 0x00 }, out _),
                ref failures);

            var selectBody = new byte[]
            {
                0x10, 0x27, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var select = SelectDungeonRequest.Parse(selectBody);
            Check(
                "A21 SELECT_DUNGEON keeps 15B body and zero hell flags",
                select.DungeonId == 10000
                && select.Difficulty == 0
                && select.HellPartyRequestFlag == 0
                && select.HellPartyDifficultyFlag == 0
                && select.A21Reserved0 == 0
                && select.A21Reserved1 == 0,
                ref failures);

            var pickupItem = DropItemBuilder.BuildPickupItem(
                srcSlot: 0x67,
                pickerActorId: 1081,
                dstInvSlot: 0x79,
                moveFlag: 7);
            Check(
                "A21 GET_ITEM item notification is 18B",
                pickupItem.Length == 18
                && BitConverter.ToUInt16(pickupItem, 0) == 0x67
                && BitConverter.ToUInt16(pickupItem, 2) == 1081
                && pickupItem[4] == 1
                && BitConverter.ToUInt16(pickupItem, 15) == 0x79,
                ref failures);

            var pickupGold = DropItemBuilder.BuildPickupGold(
                srcSlot: 0x66,
                pickerActorId: 1081,
                goldAmount: 8);
            Check(
                "A21 GET_ITEM gold notification is 117B",
                pickupGold.Length == 117
                && BitConverter.ToUInt16(pickupGold, 0) == 0x66
                && BitConverter.ToUInt16(pickupGold, 2) == 1081
                && BitConverter.ToInt32(pickupGold, 6) == 8,
                ref failures);

            var pickupAck = DropItemBuilder.BuildGetItemSuccessAck();
            Check(
                "A21 GET_ITEM success ACK is one byte",
                pickupAck.Length == 1 && pickupAck[0] == 1,
                ref failures);

            var noDropDie = DungeonNotificationBuilder.BuildMonsterDie(
                monsterSeqId: 0x66E6,
                drops: Array.Empty<DropInfo>(),
                ownerActorId: 9);
            Check(
                "A21 DIE_MONSTER without drops is 7B",
                noDropDie.Length == 7
                && BitConverter.ToUInt16(noDropDie, 0) == 0x66E6
                && noDropDie[2] == 0,
                ref failures);

            var oneGoldDropDie = DungeonNotificationBuilder.BuildMonsterDie(
                monsterSeqId: 0x66E6,
                drops: new[]
                {
                    new DropInfo
                    {
                        SceneSlot = 11,
                        TemplateId = 0,
                        StackCount = 1,
                    },
                },
                ownerActorId: 9);
            Check(
                "A21 DIE_MONSTER one-drop entry is 48B with owner at body offset 47",
                oneGoldDropDie.Length == 55
                && oneGoldDropDie[2] == 1
                && BitConverter.ToUInt16(oneGoldDropDie, 3) == 11
                && BitConverter.ToUInt16(oneGoldDropDie, 3 + 44) == 9,
                ref failures);

            var exp = ExpNotificationBuilder.Build(
                level: 1,
                totalExp: 0,
                skillPoints: default,
                honorLevel: new DfoServer.Game.Accounts.HonorLevelSummary(),
                channelBonusExp: 73);
            Check(
                "A21 EXP is 83B with channel bonus at body offset 0x4B",
                exp.Length == ExpNotificationBuilder.BodyLength
                && exp.Length == 83
                && BitConverter.ToUInt32(exp, ExpNotificationBuilder.ChannelBonusExpOffset) == 73,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_TUTORIAL_PROTOCOL selftest passed."
                    : $"A21_TUTORIAL_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
