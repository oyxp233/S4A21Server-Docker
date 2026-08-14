using DfoServer.Game.Dungeon;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using System;

namespace DfoServer.SelfTests
{
    public static class DungeonExperienceSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_EXPERIENCE selftest ===");
            var failures = 0;

            var difficultyRates = new[] { 1.3, 2.0, 2.5, 3.0, 4.0 };
            var partyRates = new[] { 1.0, 2.0, 3.0, 4.0 };
            var monsterKindRates = new[] { 1.0, 2.0, 2.0, 4.0 };
            var definition = new DungeonExperienceDefinition(
                dungeonId: 161,
                DungeonExperienceDefinitionKind.Standard,
                standardLevel: 40,
                experienceWeight: 2.4,
                difficultyRates,
                partyRates,
                monsterKindRates,
                legacyMonsterOverallRate: 1.0);

            difficultyRates[2] = 99.0;
            partyRates[1] = 99.0;
            monsterKindRates[3] = 99.0;
            Check(
                "definition defensively freezes ETC rates",
                NearlyEqual(definition.GetDifficultyRate(2), 2.5)
                    && NearlyEqual(definition.GetPartyMemberRate(2), 2.0)
                    && NearlyEqual(definition.GetMonsterKindRate(3), 4.0),
                ref failures);

            var normal = CalculateMonster(definition, monsterKind: 0);
            var champion = CalculateMonster(definition, monsterKind: 1);
            var superChampion = CalculateMonster(definition, monsterKind: 2);
            var named = CalculateMonster(
                definition,
                monsterKind: 0,
                isNamed: true);
            var boss = CalculateMonster(definition, monsterKind: 3);

            Check(
                "standard normal monster applies mob_reward / 2 before rates",
                normal.ParticipantBaseExperience == 2469,
                ref failures);
            Check(
                "monster kind rates preserve champion and super-champion x2",
                champion.ParticipantBaseExperience == 4939
                    && superChampion.ParticipantBaseExperience == 4939,
                ref failures);
            Check(
                "named multiplier remains independent from actor kind",
                named.ParticipantBaseExperience == 7408,
                ref failures);
            Check(
                "boss actor kind uses configured x4 rate",
                boss.ParticipantBaseExperience == 9878,
                ref failures);

            var premium = new PremiumEffects { BonusExpPercent = 20 };
            var normalBonus = premium.ComputeBonusExp(
                normal.ParticipantBaseExperience);
            var championBonus = premium.ComputeBonusExp(
                champion.ParticipantBaseExperience);
            var namedBonus = premium.ComputeBonusExp(
                named.ParticipantBaseExperience);
            var bossBonus = premium.ComputeBonusExp(
                boss.ParticipantBaseExperience);
            Check(
                "current supported growth contract produces verified kill totals",
                normalBonus == 493
                    && normal.ParticipantBaseExperience + normalBonus == 2962
                    && championBonus == 987
                    && champion.ParticipantBaseExperience + championBonus == 5926
                    && namedBonus == 1481
                    && named.ParticipantBaseExperience + namedBonus == 8889
                    && bossBonus == 1975
                    && boss.ParticipantBaseExperience + bossBonus == 11853,
                ref failures);
            Check(
                "current supported bonuses stay below historical video upper bounds",
                normal.ParticipantBaseExperience + normalBonus < 3110
                    && champion.ParticipantBaseExperience + championBonus < 6223
                    && named.ParticipantBaseExperience + namedBonus < 9336,
                ref failures);

            var twoPlayerNormal = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    definition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 37,
                        monsterLevel: 40,
                        difficulty: 2,
                        monsterKind: 0,
                        isNamedMonster: false,
                        partyMemberCount: 2));
            Check(
                "party rate and participant division use frozen entry count",
                twoPlayerNormal.SharedBaseExperience == 4410
                    && twoPlayerNormal.ParticipantBaseExperience == 2469,
                ref failures);

            var overLevelNormal = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    definition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 50,
                        monsterLevel: 40,
                        difficulty: 2,
                        monsterKind: 0,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            Check(
                "level difference penalty is applied after shared monster base",
                overLevelNormal.SharedBaseExperience == 2205
                    && overLevelNormal.ParticipantBaseExperience == 110,
                ref failures);

            var clear = DungeonExperienceCalculator.CalculateStandardClear(
                definition,
                new DungeonClearExperienceContext(
                    characterLevel: 37,
                    difficulty: 2,
                    totalKilledMonsterCount: 36,
                    partyMemberCount: 1));
            var clearGrowthBonus = premium.ComputeBonusExp(
                clear.ParticipantBaseExperience);
            Check(
                "36-kill clear uses character mob_reward and instance kill count",
                clear.SharedBaseExperience == 69876
                    && clear.ParticipantBaseExperience == 78261
                    && clearGrowthBonus == 15652
                    && clear.ParticipantBaseExperience < 83966,
                ref failures);
            VerifyPartyClearBreakdown(definition, ref failures);

            VerifyParticipantClearBonuses(
                definition,
                clear.ParticipantBaseExperience,
                ref failures);
            VerifyBonusInventoryFacts(ref failures);

            var participantRuntime = new DungeonParticipantExperienceRuntime();
            var frozenBonuses = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 2,
                partyHasEquippedAvatar: true,
                hasEquippedCreature: true);
            var replacementBonuses = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false);
            var firstFreeze = participantRuntime.TryFreezeBonusSnapshot(
                frozenBonuses);
            var duplicateFreeze = participantRuntime.TryFreezeBonusSnapshot(
                replacementBonuses);
            var capturedBonuses = participantRuntime.CaptureBonusSnapshot();
            Check(
                "participant bonus snapshot freezes once and rejects replacement",
                firstFreeze
                    && !duplicateFreeze
                    && capturedBonuses.PartyMemberCount == 2
                    && capturedBonuses.PartyHasEquippedAvatar
                    && capturedBonuses.HasEquippedCreature,
                ref failures);
            participantRuntime.RecordMonster(
                normal.ParticipantBaseExperience,
                normalBonus,
                isBoss: false,
                isChampion: false,
                isSuperChampion: false,
                isNamedMonster: false,
                channelBonusExperience: 50);
            participantRuntime.RecordMonster(
                champion.ParticipantBaseExperience,
                championBonus,
                isBoss: false,
                isChampion: true,
                isSuperChampion: false,
                isNamedMonster: false);
            var participantSnapshot = participantRuntime.Capture();
            Check(
                "participant runtime separates base, bonus, total, and type projection",
                participantSnapshot.MonsterBaseExperience == 7408
                    && participantSnapshot.MonsterGrowthContractBonusExperience == 1480
                    && participantSnapshot.MonsterChannelBonusExperience == 50
                    && participantSnapshot.MonsterTotalExperience == 8938
                    && participantSnapshot.ChampionBaseExperience == 4939,
                ref failures);

            VerifyClearRewardProjection(ref failures);
            VerifyNonStandardCompatibility(definition, ref failures);
            VerifyAuthoritativeCatalog(ref failures);
            VerifyChannelExperienceDefinitions(ref failures);
            VerifyMalformedChannelDefinitions(ref failures);
            VerifyChannelExperienceCalculator(ref failures);
            VerifyRankExperienceRates(ref failures);
            VerifyKillChannelExperience(ref failures);
            VerifyExpNotificationChannelProjection(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static DungeonBaseExperienceResult CalculateMonster(
            DungeonExperienceDefinition definition,
            int monsterKind,
            bool isNamed = false)
            => DungeonExperienceCalculator.CalculateStandardMonster(
                definition,
                new DungeonMonsterExperienceContext(
                    characterLevel: 37,
                    monsterLevel: 40,
                    difficulty: 2,
                    monsterKind,
                    isNamed,
                    partyMemberCount: 1));

        private static void VerifyClearRewardProjection(ref int failures)
        {
            const uint MonsterBaseExperience = 197560;
            const int MonsterGrowthBonus = 39484;
            const uint MonsterTotalExperience =
                MonsterBaseExperience + MonsterGrowthBonus;
            var body = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 78261,
                partyClearBreakdownExp: 39130,
                avatarExp: 3913,
                creatureExp: 3913,
                channelExp: 3913,
                growthContractExp: 15652,
                monsterGrowthContractExp: MonsterGrowthBonus,
                monsterExp: MonsterTotalExperience);

            const int MonsterGrowthBonusOffset = 17 + 18 * sizeof(int);
            const int PartyClearBreakdownOffset = 2 * sizeof(int);
            const int AvatarBonusOffset = 3 * sizeof(int);
            const int CreatureBonusOffset = 17 + 5 * sizeof(int);
            const int ChannelBonusOffset = 17 + 23 * sizeof(int);
            const int MonsterTotalTailOffset = 214;
            var projectedAvatarBonus = BitConverter.ToInt32(
                body,
                AvatarBonusOffset);
            var projectedClearBase = BitConverter.ToUInt32(body, 0);
            var projectedPartyClearBreakdown = BitConverter.ToUInt32(
                body,
                PartyClearBreakdownOffset);
            var projectedCreatureBonus = BitConverter.ToInt32(
                body,
                CreatureBonusOffset);
            var projectedChannelBonus = BitConverter.ToInt32(
                body,
                ChannelBonusOffset);
            var projectedKillBonus = BitConverter.ToInt32(
                body,
                MonsterGrowthBonusOffset);
            var projectedKillTotal = BitConverter.ToUInt32(
                body,
                MonsterTotalTailOffset);
            Check(
                "A14 0x0023 keeps the verified 222-byte standard shape",
                body.Length == 222,
                ref failures);
            Check(
                "A14 0x0023 field three splits party experience without changing total base",
                projectedClearBase == 78261
                    && projectedPartyClearBreakdown == 39130
                    && projectedClearBase - projectedPartyClearBreakdown
                        + projectedPartyClearBreakdown == projectedClearBase,
                ref failures);
            Check(
                "A14 0x0023 projects avatar in field four and creature in slot six",
                projectedAvatarBonus == 3913
                    && projectedCreatureBonus == 3913,
                ref failures);
            Check(
                "A14 0x0023 projects channel bonus in slot 24",
                projectedChannelBonus == 3913,
                ref failures);
            Check(
                "A14 tail projects awarded kill total rather than an intermediate base",
                projectedKillTotal == MonsterTotalExperience,
                ref failures);
            Check(
                "A14 client identity tail minus kill bonuses equals kill base",
                projectedKillTotal - projectedKillBonus
                    == MonsterBaseExperience,
                ref failures);
        }

        private static void VerifyPartyClearBreakdown(
            DungeonExperienceDefinition definition,
            ref int failures)
        {
            const uint ParticipantBaseExperience = 1200;
            var solo = DungeonExperienceCalculator.CalculatePartyClearBreakdown(
                definition,
                ParticipantBaseExperience,
                partyMemberCount: 1);
            var duo = DungeonExperienceCalculator.CalculatePartyClearBreakdown(
                definition,
                ParticipantBaseExperience,
                partyMemberCount: 2);
            var trio = DungeonExperienceCalculator.CalculatePartyClearBreakdown(
                definition,
                ParticipantBaseExperience,
                partyMemberCount: 3);
            var squad = DungeonExperienceCalculator.CalculatePartyClearBreakdown(
                definition,
                ParticipantBaseExperience,
                partyMemberCount: 4);

            Check(
                "party clear display split uses frozen one-to-four-player ETC rates",
                solo == 0
                    && duo == 600
                    && trio == 800
                    && squad == 900,
                ref failures);
            Check(
                "party clear display split fails closed for invalid event rates",
                DungeonExperienceCalculator.CalculatePartyClearBreakdown(
                    definition,
                    ParticipantBaseExperience,
                    partyMemberCount: 2,
                    partyEventBonusRate: double.NaN) == 0,
                ref failures);
        }

        private static void VerifyParticipantClearBonuses(
            DungeonExperienceDefinition definition,
            uint clearBaseExperience,
            ref int failures)
        {
            var none = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    clearBaseExperience,
                    DungeonParticipantExperienceBonusSnapshot.None);
            var soloAvatar = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    clearBaseExperience,
                    new DungeonParticipantExperienceBonusSnapshot(
                        partyMemberCount: 1,
                        partyHasEquippedAvatar: true,
                        hasEquippedCreature: false));
            var partyAvatar = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    clearBaseExperience,
                    new DungeonParticipantExperienceBonusSnapshot(
                        partyMemberCount: 2,
                        partyHasEquippedAvatar: true,
                        hasEquippedCreature: false));
            var creature = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    clearBaseExperience,
                    new DungeonParticipantExperienceBonusSnapshot(
                        partyMemberCount: 1,
                        partyHasEquippedAvatar: false,
                        hasEquippedCreature: true));
            var combined = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    clearBaseExperience,
                    new DungeonParticipantExperienceBonusSnapshot(
                        partyMemberCount: 2,
                        partyHasEquippedAvatar: true,
                        hasEquippedCreature: true));
            var minimum = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    clearBaseExperience: 1,
                    new DungeonParticipantExperienceBonusSnapshot(
                        partyMemberCount: 1,
                        partyHasEquippedAvatar: true,
                        hasEquippedCreature: true));

            Check(
                "clear participant bonuses remain zero without frozen eligibility",
                none.TotalBonusExperience == 0,
                ref failures);
            Check(
                "avatar clear bonus uses independent solo 2% and party 5% floors",
                soloAvatar.AvatarBonusExperience == 1565
                    && partyAvatar.AvatarBonusExperience == 3913,
                ref failures);
            Check(
                "equipped creature clear bonus uses independent 5% floor",
                creature.CreatureBonusExperience == 3913,
                ref failures);
            Check(
                "avatar and creature clear bonuses add without multiplying base",
                combined.AvatarBonusExperience == 3913
                    && combined.CreatureBonusExperience == 3913
                    && combined.TotalBonusExperience == 7826,
                ref failures);
            Check(
                "nonzero clear bonus components have a minimum value of one",
                minimum.AvatarBonusExperience == 1
                    && minimum.CreatureBonusExperience == 1,
                ref failures);
        }

        private static void VerifyBonusInventoryFacts(ref int failures)
        {
            var inventory = new InventoryService(
                characterId: 990881,
                accountId: 990881);
            inventory.SetItem(
                InventoryListType.Avatar,
                slotIndex: 0,
                ItemCore.Create(ItemCore.KindAvatar, itemId: 39075));
            var looseAvatar = DungeonParticipantExperienceBonusSnapshotCapture
                .CaptureInventory(inventory);
            Check(
                "avatar storage does not count as an equipped avatar",
                !looseAvatar.HasEquippedAvatar,
                ref failures);

            inventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.HatAvatar,
                ItemCore.Create(ItemCore.KindAvatar, itemId: 39075));
            var creature = ItemCore.Create(ItemCore.KindCreature, itemId: 500001);
            creature.Value = 77;
            inventory.CreatureDetails.Put(new CreatureDetail
            {
                Uid = 77,
                FieldAfterValue32 = 1,
            });
            inventory.SetItem(
                InventoryListType.Equipment,
                PetInventoryLayout.CreatureEquipSlot,
                creature);
            var equipped = DungeonParticipantExperienceBonusSnapshotCapture
                .CaptureInventory(inventory);
            Check(
                "equipment avatar slots and PetInventoryAccessor produce bonus facts",
                equipped.HasEquippedAvatar
                    && equipped.HasEquippedCreature,
                ref failures);
        }

        private static void VerifyNonStandardCompatibility(
            DungeonExperienceDefinition standard,
            ref int failures)
        {
            var risk = new DungeonExperienceDefinition(
                standard.DungeonId,
                DungeonExperienceDefinitionKind.Risk,
                standard.StandardLevel,
                standard.ExperienceWeight,
                new[] { 1.3, 2.0, 2.5, 3.0, 4.0 },
                new[] { 1.0, 2.0, 3.0, 4.0 },
                new[] { 1.0, 2.0, 2.0, 4.0 },
                legacyMonsterOverallRate: 1.0);
            var legacy = DungeonExperienceCalculator
                .CalculateNonStandardCompatibilityMonster(
                    risk,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 37,
                        monsterLevel: 40,
                        difficulty: 2,
                        monsterKind: 1,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            Check(
                "risk/tower/altar model remains outside standard kind multipliers",
                legacy.SharedBaseExperience == 4410
                    && legacy.ParticipantBaseExperience == 4939,
                ref failures);
        }

        private static void VerifyChannelExperienceDefinitions(ref int failures)
        {
            const string text = @"
[dungeon]
`[sky_catle]`
<4::channel_info_dname_2>
156
[/dungeon]

[dungeon]
`[sainthorn]`
<4::channel_info_dname_15>
200
[/dungeon]

[server]
1
11 `normal` 1 `[sky_catle]` 5 0 0 0 0
100 `special` 1 `[sainthorn]` 5 0 0 0 0
68 `duel` 24 `[none]` 0 0 0 0 0
[/server]";

            var sky = ChannelExperienceDefinitionCatalog.ResolveForTest(
                text,
                channelId: 11,
                dungeonId: 156);
            var skyMiss = ChannelExperienceDefinitionCatalog.ResolveForTest(
                text,
                channelId: 11,
                dungeonId: 161);
            var special = ChannelExperienceDefinitionCatalog.ResolveForTest(
                text,
                channelId: 100,
                dungeonId: 200);
            var duel = ChannelExperienceDefinitionCatalog.ResolveForTest(
                text,
                channelId: 68,
                dungeonId: 200);
            var unknown = ChannelExperienceDefinitionCatalog.ResolveForTest(
                text,
                channelId: 999,
                dungeonId: 156);

            Check(
                "channel parser resolves classification whitelist and 5%",
                sky.BonusRate == 0.05
                    && sky.ChannelType == 1,
                ref failures);
            Check(
                "channel parser rejects an unlisted dungeon",
                skyMiss.BonusRate == 0.0,
                ref failures);
            Check(
                "channel parser resolves CH.100 independently",
                special.BonusRate == 0.05
                    && special.ChannelId == 100,
                ref failures);
            Check(
                "none and unknown channels remain zero",
                duel.BonusRate == 0.0
                    && unknown.BonusRate == 0.0,
                ref failures);

            var actualSky = ChannelExperienceDefinitionCatalog.Resolve(11, 156);
            var actualShark = ChannelExperienceDefinitionCatalog.Resolve(11, 161);
            var actualSainthorn = ChannelExperienceDefinitionCatalog.Resolve(100, 200);
            Check(
                "authoritative channel_info.etc matches CH.11/CH.100 boundaries",
                ChannelExperienceDefinitionCatalog.ConfiguredChannelCountForTest() >= 2
                    && actualSky.BonusRate == 0.05
                    && actualShark.BonusRate == 0.0
                    && actualSainthorn.BonusRate == 0.05,
                ref failures);
        }

        private static void VerifyChannelExperienceCalculator(ref int failures)
        {
            var enabled = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false,
                channelId: 11,
                channelType: 1,
                channelExperienceBonusRate: 0.05);
            var disabled = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false,
                channelId: 11,
                channelType: 1,
                channelExperienceBonusRate: 0.0);

            Check(
                "channel bonus uses frozen clear base and floors normally",
                DungeonExperienceCalculator.CalculateChannelClearBonus(
                    1000,
                    enabled) == 50
                    && DungeonExperienceCalculator.CalculateChannelClearBonus(
                        19,
                        enabled) == 0,
                ref failures);
            Check(
                "channel bonus is zero when the frozen entry has no rate",
                DungeonExperienceCalculator.CalculateChannelClearBonus(
                    1000,
                    disabled) == 0,
                ref failures);
        }

        private static void VerifyRankExperienceRates(ref int failures)
        {
            Check(
                "PVF rank 99/90/80/60/50 maps to 20/15/12/10/5 percent",
                MonsterRewardTable.GetClearRankBonusIndex(99) == 4
                    && NearlyEqual(
                        MonsterRewardTable.GetClearRankExpBonusRate(4),
                        0.20)
                    && MonsterRewardTable.GetClearRankBonusIndex(90) == 3
                    && NearlyEqual(
                        MonsterRewardTable.GetClearRankExpBonusRate(3),
                        0.15)
                    && MonsterRewardTable.GetClearRankBonusIndex(80) == 2
                    && NearlyEqual(
                        MonsterRewardTable.GetClearRankExpBonusRate(2),
                        0.12)
                    && MonsterRewardTable.GetClearRankBonusIndex(60) == 1
                    && NearlyEqual(
                        MonsterRewardTable.GetClearRankExpBonusRate(1),
                        0.10)
                    && MonsterRewardTable.GetClearRankBonusIndex(50) == 0
                    && NearlyEqual(
                        MonsterRewardTable.GetClearRankExpBonusRate(0),
                        0.05)
                    && MonsterRewardTable.GetClearRankBonusIndex(49) == -1,
                ref failures);
        }

        private static void VerifyKillChannelExperience(ref int failures)
        {
            var enabled = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false,
                channelId: 11,
                channelType: 1,
                channelExperienceBonusRate: 0.05);
            var disabled = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false,
                channelId: 11,
                channelType: 1,
                channelExperienceBonusRate: 0.0);

            Check(
                "kill channel bonus uses frozen monster base and floor",
                DungeonExperienceCalculator.CalculateChannelMonsterBonus(
                    1000,
                    enabled) == 50
                    && DungeonExperienceCalculator.CalculateChannelMonsterBonus(
                        19,
                        enabled) == 0
                    && DungeonExperienceCalculator.CalculateChannelMonsterBonus(
                        1000,
                        disabled) == 0,
                ref failures);
        }

        private static void VerifyExpNotificationChannelProjection(
            ref int failures)
        {
            var body = ExpNotificationBuilder.Build(
                level: 37,
                totalExp: 1000,
                skillPoints: default,
                honorLevel: new HonorLevelSummary(),
                channelBonusExp: 73);
            Check(
                "0x0025 projects channel kill bonus at +0x4F",
                body.Length == ExpNotificationBuilder.BodyLength
                    && BitConverter.ToUInt32(body, 0x4F) == 73,
                ref failures);
        }

        private static void VerifyMalformedChannelDefinitions(ref int failures)
        {
            const string malformedRate = @"
[dungeon]
`[sky_catle]`
156
[/dungeon]

[server]
1
11 `normal` 1 `[sky_catle]` invalid 0 0 0 0
[/server]";
            const string duplicateChannel = @"
[dungeon]
`[sky_catle]`
156
[/dungeon]

[server]
2
11 `normal` 1 `[sky_catle]` 5 0 0 0 0
11 `normal-duplicate` 1 `[sky_catle]` 5 0 0 0 0
[/server]";
            const string duplicateGroup = @"
[dungeon]
`[sky_catle]`
156
[/dungeon]

[dungeon]
`[sky_catle]`
157
[/dungeon]

[server]
1
11 `normal` 1 `[sky_catle]` 5 0 0 0 0
[/server]";

            var malformed = ChannelExperienceDefinitionCatalog.ResolveForTest(
                malformedRate,
                channelId: 11,
                dungeonId: 156);
            var duplicate = ChannelExperienceDefinitionCatalog.ResolveForTest(
                duplicateChannel,
                channelId: 11,
                dungeonId: 156);
            var duplicateClassification =
                ChannelExperienceDefinitionCatalog.ResolveForTest(
                    duplicateGroup,
                    channelId: 11,
                    dungeonId: 156);

            Check(
                "malformed channel rate fails closed",
                malformed.BonusRate == 0.0,
                ref failures);
            Check(
                "duplicate channel definition fails closed",
                duplicate.BonusRate == 0.0,
                ref failures);
            Check(
                "duplicate dungeon classification fails closed",
                duplicateClassification.BonusRate == 0.0,
                ref failures);
        }

        private static void VerifyAuthoritativeCatalog(ref int failures)
        {
            var resolved = DungeonExperienceDefinitionCatalog.Resolve(161);
            Check(
                "authoritative PVF resolves dungeon 161 immutable experience definition",
                resolved.IsAvailable
                    && resolved.Kind == DungeonExperienceDefinitionKind.Standard
                    && resolved.StandardLevel == 40
                    && NearlyEqual(resolved.ExperienceWeight, 2.4)
                    && NearlyEqual(resolved.GetDifficultyRate(2), 2.5)
                    && NearlyEqual(resolved.GetPartyMemberRate(4), 4.0)
                    && NearlyEqual(resolved.GetMonsterKindRate(3), 4.0),
                ref failures);

            var instance = new DungeonInstance(
                dungeonId: 161,
                difficulty: 2,
                DungeonRewardPolicy.Standard,
                DungeonDropDefinition.CreateStandard(161),
                resolved);
            Check(
                "DungeonInstance freezes the resolved experience definition",
                ReferenceEquals(instance.ExperienceDefinition, resolved),
                ref failures);
        }

        private static bool NearlyEqual(double left, double right)
            => Math.Abs(left - right) < 0.000001;

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"  {(condition ? "PASS" : "FAIL")} {name}");
            if (!condition)
                failures++;
        }
    }
}
