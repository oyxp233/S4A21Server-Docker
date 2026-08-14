using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using System;
using System.Linq;
using System.Net.Sockets;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class ImpossibleDungeonDropSelfTest
    {
        private const int PartyGuardianMasterMonsterCode = 56108;
        private const int SoloGuardianMasterMonsterCode = 57040;
        private const int DimensionFragmentItemId = 3311;
        private const int BakalBossMonsterCode = 61238;
        private const int BakalNormalPoolIndex = 101;
        private const uint BakalExternalPoolHitSeed = 58;
        private const int GuardianNormalPoolIndex = 61;
        private const int GuardianMasterPoolIndex = 63;
        private const int GuardianKingPoolIndex = 64;
        private const int MaleSlayerWeaponItemId = 365012;
        private const int FemaleFighterWeaponItemId = 385000;

        public static int Run()
        {
            Console.WriteLine("=== IMPOSSIBLE_DUNGEON_DROP selftest ===");
            var failures = 0;

            var entries = Dungeon.LoadDungeonLstFile().Entries
                .Where(entry => IsImpossibleFixturePath(entry.FilePath))
                .ToList();
            var definitions = entries
                .Select(entry => DungeonDropDefinitionCatalog.Resolve(entry.Id))
                .ToList();
            var partyDefinitions = definitions
                .Where(definition => definition.Kind
                    == DungeonDropDefinitionKind.ImpossibleParty)
                .ToList();
            var soloDefinitions = definitions
                .Where(definition => definition.Kind
                    == DungeonDropDefinitionKind.ImpossibleSolo)
                .ToList();

            Check(
                "current PVF exposes six party and six solo impossible definitions",
                entries.Count == 12
                    && partyDefinitions.Count == 6
                    && soloDefinitions.Count == 6,
                ref failures);

            var reciprocalPairs = partyDefinitions.Count == 6;
            foreach (var party in partyDefinitions)
            {
                var solo = soloDefinitions.FirstOrDefault(
                    candidate => candidate.DungeonId == party.SharedDungeonId);
                if (solo == null
                    || solo.SharedDungeonId != party.DungeonId
                    || solo.ImpossibleClassification
                        != party.ImpossibleClassification)
                {
                    reciprocalPairs = false;
                    break;
                }
            }
            Check(
                "party and solo definitions are paired by reciprocal shared indexes",
                reciprocalPairs,
                ref failures);

            var expectedSources = DungeonMonsterDropSource.Gold
                | DungeonMonsterDropSource.Independent;
            Check(
                "impossible definitions allow only gold and ETC independent items",
                definitions.All(definition =>
                    definition.Policy.AllowedSources == expectedSources
                    && !definition.Policy.Allows(
                        DungeonMonsterDropSource.GenericItems)
                    && !definition.Policy.Allows(
                        DungeonMonsterDropSource.MonsterTemplateItems)
                    && !definition.Policy.Allows(
                        DungeonMonsterDropSource.AreaMaterials)
                    && !definition.Policy.Allows(
                        DungeonMonsterDropSource.World)),
                ref failures);

            var standard = DungeonDropPolicy.Standard;
            Check(
                "standard dungeons retain every existing monster drop source",
                standard.AllowedSources == DungeonMonsterDropSource.All,
                ref failures);

            var mappedJobGroup =
                IndependentDropDefinitionCatalog.ResolveChronicleDropJobGroup(
                    characterJob: 0,
                    growType: 4);
            var packedGrowTypeJobGroup =
                IndependentDropDefinitionCatalog.ResolveChronicleDropJobGroup(
                    characterJob: 0,
                    growType: 0x14);
            Check(
                "chronicle job mapping uses character job and low grow-type nibble",
                mappedJobGroup == 4 && packedGrowTypeJobGroup == 4,
                ref failures);

            var externalPoolListCount = LstFile.Parse(
                PvfArchiveAccessor.ReadText("Etc/IndependentDrop.lst"))
                .Entries.Count;
            Check(
                "every current external-list definition is parsed into the catalog",
                externalPoolListCount > 0
                    && IndependentDropDefinitionCatalog.ExternalPoolCount
                        == externalPoolListCount,
                ref failures);

            TestGuardianDifficultyPools(ref failures);

            var groupOneFound =
                IndependentDropDefinitionCatalog.TryResolveExternalPool(
                    BakalNormalPoolIndex,
                    chronicleDropJobGroup: 1,
                    out var groupOnePool);
            var groupFourFound =
                IndependentDropDefinitionCatalog.TryResolveExternalPool(
                    BakalNormalPoolIndex,
                    chronicleDropJobGroup: 4,
                    out var groupFourPool);
            Check(
                "external listFlag=2 pool is parsed as item/weight/job-group triples",
                groupOneFound
                    && groupFourFound
                    && groupOnePool.Items.Any(item =>
                        item.ItemId == MaleSlayerWeaponItemId)
                    && !groupOnePool.Items.Any(item =>
                        item.ItemId == FemaleFighterWeaponItemId)
                    && groupFourPool.Items.Any(item =>
                        item.ItemId == FemaleFighterWeaponItemId)
                    && !groupFourPool.Items.Any(item =>
                        item.ItemId == MaleSlayerWeaponItemId)
                    && groupOnePool.Items.All(item => item.ItemId > 32)
                    && groupFourPool.Items.All(item => item.ItemId > 32),
                ref failures);

            var bakalEntryFound =
                IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    BakalBossMonsterCode,
                    out var bakalEntries)
                && bakalEntries.Any(entry =>
                    entry.PoolKind == IndependentDropPoolKind.External
                    && entry.GetProbability(difficultyIndex: 0) > 0
                    && entry.PoolIndexes.Contains(BakalNormalPoolIndex));
            Check(
                "Bakal boss normal difficulty references its dedicated pool",
                bakalEntryFound,
                ref failures);

            ushort bakalSlotCounter = 0;
            var bakalDrops = IndependentDropSystem.GenerateDrops(
                BakalBossMonsterCode,
                difficulty: 0,
                dungeonLevel: 70,
                partyMemberCount: 1,
                chronicleDropJobGroup: 4,
                lcg: new DnfLcg(BakalExternalPoolHitSeed),
                slotCounter: ref bakalSlotCounter);
            var groupFourItemIds = groupFourFound
                ? groupFourPool.Items.Select(item => item.ItemId).ToHashSet()
                : new System.Collections.Generic.HashSet<int>();
            var groupOneOnlyItemIds = groupOneFound
                ? groupOnePool.Items
                    .Select(item => item.ItemId)
                    .Where(itemId => !groupFourItemIds.Contains(itemId))
                    .ToHashSet()
                : new System.Collections.Generic.HashSet<int>();
            Check(
                "runtime emits the monster pool only for the frozen participant job group",
                bakalDrops.Any(drop =>
                    groupFourItemIds.Contains((int)drop.TemplateId))
                    && !bakalDrops.Any(drop =>
                        groupOneOnlyItemIds.Contains((int)drop.TemplateId)),
                ref failures);

            var injectedPool = new[]
            {
                new MonsterDropTable.DropPoolEntry
                {
                    ItemId = 987654321,
                    Weight = 1,
                },
            };
            var restrictedItemLeak = false;
            for (uint seed = 0; seed < 256 && !restrictedItemLeak; seed++)
            {
                ushort slotCounter = 0;
                var generator = new DropGenerator(new DnfLcg(seed));
                var (_, drops) = generator.GenerateMonsterDrops(
                    monsterLevel: 70,
                    monsterType: 0,
                    monsterCode: -392001,
                    difficulty: 0,
                    dungeonLevel: 70,
                    partyMemberCount: 1,
                    chronicleDropJobGroup: -1,
                    dropPolicy: DungeonDropPolicy.Impossible,
                    slotCounter: ref slotCounter,
                    dropPool: injectedPool);
                restrictedItemLeak = drops.Any(drop => !drop.IsGold);
            }
            Check(
                "impossible generation cannot project generic, world, or injected items",
                !restrictedItemLeak,
                ref failures);

            var partyProbabilityFound =
                IndependentDropSystem.TryGetDirectItemProbability(
                    PartyGuardianMasterMonsterCode,
                    difficulty: 0,
                    itemId: DimensionFragmentItemId,
                    out var partyProbability);
            var soloProbabilityFound =
                IndependentDropSystem.TryGetDirectItemProbability(
                    SoloGuardianMasterMonsterCode,
                    difficulty: 0,
                    itemId: DimensionFragmentItemId,
                    out var soloProbability);
            Check(
                "solo rate comes from its own ETC monster rule and is lower",
                partyProbabilityFound
                    && soloProbabilityFound
                    && partyProbability == 500000
                    && soloProbability == 166000
                    && soloProbability < partyProbability,
                ref failures);

            if (partyDefinitions.Count > 0)
            {
                var definition = partyDefinitions[0];
                var instance = new DungeonInstance(
                    checked((short)definition.DungeonId),
                    difficulty: 0,
                    rewardPolicy: DungeonRewardPolicy.Standard,
                    dropDefinition: definition);
                var leaderRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    runGeneration: 1,
                    DungeonRunState.Active);
                var memberRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    runGeneration: 1,
                    DungeonRunState.Active);
                leaderRun.ChronicleDropJobGroup = 4;
                memberRun.ChronicleDropJobGroup = 1;
                Check(
                    "party participants share one frozen drop definition",
                    ReferenceEquals(leaderRun.Instance, memberRun.Instance)
                        && ReferenceEquals(
                            leaderRun.DropDefinition,
                            memberRun.DropDefinition)
                        && leaderRun.DropDefinition.Kind
                            == DungeonDropDefinitionKind.ImpossibleParty,
                    ref failures);
                Check(
                    "party participants keep independent personal job-group snapshots",
                    leaderRun.ChronicleDropJobGroup == 4
                        && memberRun.ChronicleDropJobGroup == 1,
                    ref failures);

                using var client = new TcpClient();
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                session.Player.Job = 0;
                session.Player.GrowType = 4;
                DungeonRunLifecycle.BeginRun(
                    session,
                    definition.DungeonId,
                    difficulty: 0);
                Check(
                    "run lifecycle freezes the catalog definition before entry",
                    session.Player.CurrentRun != null
                        && session.Player.CurrentRun.DropDefinition.DungeonId
                            == definition.DungeonId
                        && session.Player.CurrentRun.DropDefinition.Kind
                            == DungeonDropDefinitionKind.ImpossibleParty
                        && session.Player.CurrentRun.ChronicleDropJobGroup == 4,
                    ref failures);
                DungeonRunLifecycle.EndRunOnTeardown(
                    session,
                    "impossible-drop-selftest");
            }

            Console.WriteLine(
                failures == 0
                    ? "IMPOSSIBLE_DUNGEON_DROP selftest passed."
                    : $"IMPOSSIBLE_DUNGEON_DROP selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestGuardianDifficultyPools(ref int failures)
        {
            Check(
                "external equipment pools use hundred-million probability precision",
                IndependentDropSystem.GetProbabilityDenominator(
                    IndependentDropPoolKind.External) == 100_000_000
                    && IndependentDropSystem.IsProbabilityHit(
                        IndependentDropPoolKind.External,
                        probability: 4_000_000,
                        roll: 3_999_999)
                    && !IndependentDropSystem.IsProbabilityHit(
                        IndependentDropPoolKind.External,
                        probability: 4_000_000,
                        roll: 4_000_000),
                ref failures);
            Check(
                "direct and inline independent drops retain million precision",
                IndependentDropSystem.GetProbabilityDenominator(
                    IndependentDropPoolKind.None) == 1_000_000
                    && IndependentDropSystem.GetProbabilityDenominator(
                        IndependentDropPoolKind.Inline) == 1_000_000
                    && IndependentDropSystem.IsProbabilityHit(
                        IndependentDropPoolKind.None,
                        probability: 6_000,
                        roll: 5_999)
                    && !IndependentDropSystem.IsProbabilityHit(
                        IndependentDropPoolKind.None,
                        probability: 6_000,
                        roll: 6_000),
                ref failures);

            var expectedPoolIndexes = new[]
            {
                GuardianNormalPoolIndex,
                GuardianMasterPoolIndex,
                GuardianKingPoolIndex,
            };
            var difficultyPoolsMatch =
                IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    PartyGuardianMasterMonsterCode,
                    out var partyEntries);
            if (difficultyPoolsMatch)
            {
                for (var difficulty = 0;
                    difficulty < expectedPoolIndexes.Length;
                    difficulty++)
                {
                    var activeEntries = partyEntries
                        .Where(entry =>
                            entry.PoolKind == IndependentDropPoolKind.External
                            && entry.GetProbability(difficulty) > 0)
                        .ToArray();
                    difficultyPoolsMatch = activeEntries.Length == 1
                        && activeEntries[0].PoolIndexes.Count == 1
                        && activeEntries[0].PoolIndexes[0]
                            == expectedPoolIndexes[difficulty]
                        && activeEntries[0].GetProbability(difficulty)
                            == 4_000_000;
                    if (!difficultyPoolsMatch)
                        break;
                }
            }
            Check(
                "party impossible difficulty selects pools 61, 63, and 64 without merging",
                difficultyPoolsMatch,
                ref failures);

            var difficultyPoolSignatures = new string[3];
            var equipmentPartSignatures = new string[3];
            var distinctDifficultyDefinitions = true;
            for (var index = 0; index < expectedPoolIndexes.Length; index++)
            {
                if (!IndependentDropDefinitionCatalog.TryResolveExternalPool(
                        expectedPoolIndexes[index],
                        chronicleDropJobGroup: 1,
                        out var pool))
                {
                    distinctDifficultyDefinitions = false;
                    break;
                }

                difficultyPoolSignatures[index] = string.Join(
                    "|",
                    pool.Items
                        .OrderBy(item => item.ItemId)
                        .Select(item => $"{item.ItemId}:{item.Weight}"));
                equipmentPartSignatures[index] = string.Join(
                    "|",
                    pool.Items
                        .Select(item => ItemMetadataResolver.ResolveEquipmentType(
                            item.ItemId))
                        .Where(type => !string.IsNullOrEmpty(type))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(type => type, StringComparer.OrdinalIgnoreCase));
            }
            if (distinctDifficultyDefinitions)
            {
                distinctDifficultyDefinitions = difficultyPoolSignatures
                    .All(signature => !string.IsNullOrEmpty(signature))
                    && difficultyPoolSignatures.Distinct().Count() == 3;
            }
            Check(
                "difficulty pools preserve distinct configured item and weight definitions",
                distinctDifficultyDefinitions,
                ref failures);
            Check(
                "party impossible equipment parts follow the selected difficulty pool",
                equipmentPartSignatures[0] == "[amulet]|[wrist]"
                    && equipmentPartSignatures[1] == "[amulet]|[shoes]"
                    && equipmentPartSignatures[2] == "[amulet]|[shoes]",
                ref failures);

            var soloPoolMatches =
                IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    SoloGuardianMasterMonsterCode,
                    out var soloEntries);
            if (soloPoolMatches)
            {
                var activeSoloEntries = soloEntries
                    .Where(entry =>
                        entry.PoolKind == IndependentDropPoolKind.External
                        && entry.GetProbability(difficultyIndex: 0) > 0)
                    .ToArray();
                soloPoolMatches = activeSoloEntries.Length == 1
                    && activeSoloEntries[0].PoolIndexes.Count == 1
                    && activeSoloEntries[0].PoolIndexes[0]
                        == GuardianNormalPoolIndex
                    && activeSoloEntries[0].GetProbability(0) == 4_000_000;
            }
            Check(
                "solo Guardian Master keeps its PVF-selected normal equipment pool",
                soloPoolMatches,
                ref failures);
        }

        private static bool IsImpossibleFixturePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalized = path.Replace('\\', '/');
            return normalized.StartsWith(
                    "Impossible/",
                    StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith(
                    ".dgn",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
