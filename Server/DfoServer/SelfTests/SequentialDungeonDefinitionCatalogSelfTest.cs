using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class SequentialDungeonDefinitionCatalogSelfTest
    {
        private const string OverlappingConfig = @"
[sequential dungeon]
28
[dungeon index check]
225 243 244 245 246 247
[/dungeon index check]
[monster index check]
56639
[/monster index check]
[/sequential dungeon]
[sequential dungeon]
41
[dungeon index check]
243 244 245 246 247
[/dungeon index check]
[monster index check]
56675
56678
[/monster index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
247
[/entrance except dungeon]
[rewardable dungeon index]
247
[/rewardable dungeon index]
[clear reward item]
915 10157831 0
10 10157832 1
[/clear reward item]
[always visible dungeon]
247
[/always visible dungeon]
[/sequential dungeon]";

        public static int Run()
        {
            Console.WriteLine(
                "=== SEQUENTIAL_DUNGEON_DEFINITION_CATALOG selftest ===");
            var failures = 0;

            VerifyEtcProjectionAndIndexes(ref failures);
            VerifyImmutableModelValidation(ref failures);
            VerifyAmbiguousCapabilitiesFailClosed(ref failures);
            VerifyUnclosedStructuresFailClosed(ref failures);
            VerifyMalformedDefinitionsAreNotPublished(ref failures);
            VerifyEmptyEtcFailClosed(ref failures);
            VerifyMissingEtcLoadFailClosed(ref failures);
            VerifyCurrentPvfAndInstanceFreeze(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "SEQUENTIAL_DUNGEON_DEFINITION_CATALOG selftest passed."
                    : "SEQUENTIAL_DUNGEON_DEFINITION_CATALOG selftest failed: "
                        + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyImmutableModelValidation(ref int failures)
        {
            var sourceDungeonIds = new List<int> { 10, 11 };
            var definition = new SequentialDungeonDefinition(
                1,
                0,
                true,
                sourceDungeonIds,
                Array.Empty<int>(),
                showIndividualProcess: false,
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<SequentialDungeonRewardGroup>());
            sourceDungeonIds[0] = 99;
            Check(
                "definition copies caller-owned collections",
                definition.IsAntonDungeonSequence
                && definition.DungeonIds.SequenceEqual(new[] { 10, 11 }),
                ref failures);
            Check(
                "definition rejects a non-positive group key",
                ThrowsArgument(() => new SequentialDungeonDefinition(
                    0,
                    0,
                    false,
                    new[] { 10 },
                    Array.Empty<int>(),
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<SequentialDungeonRewardGroup>())),
                ref failures);
            Check(
                "definition rejects duplicate dungeon identifiers",
                ThrowsArgument(() => new SequentialDungeonDefinition(
                    1,
                    0,
                    false,
                    new[] { 10, 10 },
                    Array.Empty<int>(),
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<SequentialDungeonRewardGroup>())),
                ref failures);
            Check(
                "definition rejects route masks wider than 31 prerequisites",
                ThrowsArgument(() => new SequentialDungeonDefinition(
                    1,
                    0,
                    false,
                    Enumerable.Range(1, 32),
                    Array.Empty<int>(),
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<SequentialDungeonRewardGroup>())),
                ref failures);
            Check(
                "reward group rejects invalid values",
                ThrowsArgument(() =>
                    new SequentialDungeonRewardGroup(0, 7001, 0))
                && ThrowsArgument(() =>
                    new SequentialDungeonRewardGroup(1, 0, 0))
                && ThrowsArgument(() =>
                    new SequentialDungeonRewardGroup(1, 7001, -1)),
                ref failures);
        }

        private static void VerifyEtcProjectionAndIndexes(ref int failures)
        {
            IReadOnlyList<int> resolvedDungeonIds = null;
            var classifiedDungeonIds = new List<IReadOnlyList<int>>();
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                OverlappingConfig,
                dungeonIds =>
                {
                    resolvedDungeonIds = dungeonIds.ToArray();
                    return (byte)2;
                },
                dungeonIds =>
                {
                    classifiedDungeonIds.Add(dungeonIds.ToArray());
                    return dungeonIds.Count == 5;
                });
            var foundByKey = catalog.TryGetByGroupKey(41, out var byKey);

            Check(
                "key lookup keeps ETC order",
                foundByKey
                && byKey.DungeonIds.SequenceEqual(
                    new[] { 243, 244, 245, 246, 247 })
                && byKey.PrerequisiteDungeonIds.SequenceEqual(
                    new[] { 243, 244, 245, 246 }),
                ref failures);
            Check(
                "difficulty resolver receives ETC dungeon order",
                resolvedDungeonIds != null
                && resolvedDungeonIds.SequenceEqual(
                    new[] { 243, 244, 245, 246, 247 })
                && byKey != null
                && byKey.Difficulty == 2,
                ref failures);
            Check(
                "classification resolver freezes typed Anton capability",
                classifiedDungeonIds.Count == 2
                && classifiedDungeonIds[0].SequenceEqual(
                    new[] { 225, 243, 244, 245, 246, 247 })
                && classifiedDungeonIds[1].SequenceEqual(
                    new[] { 243, 244, 245, 246, 247 })
                && catalog.TryGetByGroupKey(28, out var generic)
                && !generic.IsAntonDungeonSequence
                && byKey != null
                && byKey.IsAntonDungeonSequence,
                ref failures);
            Check(
                "show-individual definition resolves overlap",
                catalog.TryResolvePrimaryByDungeonId(243, out var primary)
                && primary.GroupKey == 41,
                ref failures);
            Check(
                "entrance and reward capabilities resolve uniquely",
                catalog.TryResolveEntranceByDungeonId(247, out var entrance)
                && byKey != null
                && ReferenceEquals(entrance, byKey)
                && catalog.TryResolveRewardableByDungeonId(
                    247,
                    out var rewardable)
                && ReferenceEquals(rewardable, byKey),
                ref failures);
            Check(
                "monster membership uses ETC union",
                catalog.ContainsConfiguredMonster(243, 56639)
                && catalog.ContainsConfiguredMonster(243, 56675)
                && catalog.ContainsConfiguredMonster(243, 56678)
                && !catalog.ContainsConfiguredMonster(243, 99999),
                ref failures);
            Check(
                "reward tuple preserves group and card state",
                byKey != null
                && byKey.ClearRewardGroups.Count == 2
                && byKey.ClearRewardGroups[0].Weight == 915
                && byKey.ClearRewardGroups[0].RewardGroupItemId == 10157831
                && byKey.ClearRewardGroups[0].CardState == 0
                && byKey.ClearRewardGroups[1].Weight == 10
                && byKey.ClearRewardGroups[1].RewardGroupItemId == 10157832
                && byKey.ClearRewardGroups[1].CardState == 1,
                ref failures);
            Check(
                "definition collections are read-only snapshots",
                byKey != null
                && byKey.DungeonIds is IList<int> dungeonIds
                && dungeonIds.IsReadOnly
                && byKey.PrerequisiteDungeonIds is IList<int> prerequisites
                && prerequisites.IsReadOnly
                && byKey.ClearRewardGroups
                    is IList<SequentialDungeonRewardGroup> rewards
                && rewards.IsReadOnly,
                ref failures);
        }

        private static void VerifyAmbiguousCapabilitiesFailClosed(
            ref int failures)
        {
            const string ambiguousConfig = @"
[sequential dungeon]
80
[dungeon index check]
300 301
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
301
[/entrance except dungeon]
[rewardable dungeon index]
301
[/rewardable dungeon index]
[/sequential dungeon]
[sequential dungeon]
81
[dungeon index check]
300 301
[/dungeon index check]
[show individual process]
[/show individual process]
[entrance except dungeon]
301
[/entrance except dungeon]
[rewardable dungeon index]
301
[/rewardable dungeon index]
[/sequential dungeon]";
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                ambiguousConfig,
                _ => (byte)1);

            Check(
                "ambiguous primary capability fails closed",
                !catalog.TryResolvePrimaryByDungeonId(300, out _)
                && catalog.ResolvePrimaryByDungeonId(300, out _)
                    == SequentialDungeonCapabilityResolution.Ambiguous,
                ref failures);
            Check(
                "ambiguous entrance capability fails closed",
                !catalog.TryResolveEntranceByDungeonId(301, out _)
                && catalog.ResolveEntranceByDungeonId(301, out _)
                    == SequentialDungeonCapabilityResolution.Ambiguous,
                ref failures);
            Check(
                "ambiguous reward capability fails closed",
                !catalog.TryResolveRewardableByDungeonId(301, out _)
                && catalog.ResolveRewardableByDungeonId(301, out _)
                    == SequentialDungeonCapabilityResolution.Ambiguous,
                ref failures);
            Check(
                "unconfigured capability is explicitly absent",
                catalog.ResolveEntranceByDungeonId(999, out _)
                    == SequentialDungeonCapabilityResolution.Absent,
                ref failures);
        }

        private static void VerifyMalformedDefinitionsAreNotPublished(
            ref int failures)
        {
            const string malformedConfig = @"
[sequential dungeon]
91
[dungeon index check]
400 400
[/dungeon index check]
[/sequential dungeon]
[sequential dungeon]
92
[dungeon index check]
401 402
[/dungeon index check]
[clear reward item]
1 7001 -1
[/clear reward item]
[/sequential dungeon]
[sequential dungeon]
94
[dungeon index check]
405 invalid-token
[/dungeon index check]
[/sequential dungeon]
[sequential dungeon]
95
[dungeon index check]
406 407
[/dungeon index check]
[clear reward item]
[/clear reward item]
[/sequential dungeon]
[sequential dungeon]
96
[dungeon index check]
408 409
[/dungeon index check]
[clear reward item]
2147483647 7001 0
1 7002 1
[/clear reward item]
[/sequential dungeon]
[sequential dungeon]
97
[dungeon index check]
500 501 502 503 504 505 506 507
508 509 510 511 512 513 514 515
516 517 518 519 520 521 522 523
524 525 526 527 528 529 530 531
[/dungeon index check]
[/sequential dungeon]
[sequential dungeon]
98
[dungeon index check]
600 601
[/dungeon index check]
[/sequential dungeon]
[sequential dungeon]
98
[dungeon index check]
602 603
[/dungeon index check]
[/sequential dungeon]
[sequential dungeon]
93
[dungeon index check]
403 404
[/dungeon index check]
[/sequential dungeon]";
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                malformedConfig,
                _ => (byte)1);

            Check(
                "duplicate dungeon identifier fails closed",
                !catalog.TryGetByGroupKey(91, out _),
                ref failures);
            Check(
                "negative reward card state fails closed",
                !catalog.TryGetByGroupKey(92, out _),
                ref failures);
            Check(
                "illegal list token fails closed",
                !catalog.TryGetByGroupKey(94, out _),
                ref failures);
            Check(
                "explicit empty reward node fails closed",
                !catalog.TryGetByGroupKey(95, out _),
                ref failures);
            Check(
                "cumulative reward weight overflow fails closed",
                !catalog.TryGetByGroupKey(96, out _),
                ref failures);
            Check(
                "protocol prerequisite capacity overflow fails closed",
                !catalog.TryGetByGroupKey(97, out _),
                ref failures);
            Check(
                "duplicate group key fails closed",
                !catalog.TryGetByGroupKey(98, out _),
                ref failures);
            Check(
                "malformed groups do not hide an adjacent valid definition",
                catalog.TryGetByGroupKey(93, out _)
                && catalog.Definitions.Count == 1,
                ref failures);
        }

        private static void VerifyUnclosedStructuresFailClosed(
            ref int failures)
        {
            var fields = new[]
            {
                (Tag: "dungeon index check", Data: "700 701"),
                (Tag: "monster index check", Data: "57000"),
                (Tag: "entrance except dungeon", Data: "701"),
                (Tag: "rewardable dungeon index", Data: "701"),
                (Tag: "always visible dungeon", Data: "701"),
                (Tag: "clear reward item", Data: "1 7001 0"),
            };

            for (var index = 0; index < fields.Length; index++)
            {
                var invalidKey = 110 + index;
                var config = BuildValidDefinition(109)
                    + BuildUnclosedFieldDefinition(
                        invalidKey,
                        fields[index].Tag,
                        fields[index].Data);
                var catalog = SequentialDungeonDefinitionCatalog.Parse(
                    config,
                    _ => (byte)1);
                Check(
                    $"unclosed {fields[index].Tag} rejects only its definition",
                    catalog.TryGetByGroupKey(109, out _)
                    && !catalog.TryGetByGroupKey(invalidKey, out _)
                    && catalog.Definitions.Count == 1,
                    ref failures);
            }

            var implicitFlagCatalog =
                SequentialDungeonDefinitionCatalog.Parse(
                    BuildUnclosedFieldDefinition(
                        117,
                        "show individual process",
                        string.Empty)
                    + BuildValidDefinition(118),
                    _ => (byte)1);
            Check(
                "empty show-individual flag is a legal implicit flag node",
                implicitFlagCatalog.TryGetByGroupKey(
                    117,
                    out var implicitFlag)
                && implicitFlag.ShowIndividualProcess
                && implicitFlagCatalog.TryGetByGroupKey(118, out _)
                && implicitFlagCatalog.Definitions.Count == 2,
                ref failures);

            var invalidFlagCatalog =
                SequentialDungeonDefinitionCatalog.Parse(
                    BuildValidDefinition(119)
                    + BuildUnclosedFieldDefinition(
                        120,
                        "show individual process",
                        "unexpected-data"),
                    _ => (byte)1);
            Check(
                "unclosed show-individual flag with data fails closed",
                invalidFlagCatalog.TryGetByGroupKey(119, out _)
                && !invalidFlagCatalog.TryGetByGroupKey(120, out _)
                && invalidFlagCatalog.Definitions.Count == 1,
                ref failures);

            var unclosedSectionCatalog =
                SequentialDungeonDefinitionCatalog.Parse(
                    BuildValidDefinition(121)
                    + @"
[sequential dungeon]
122
[dungeon index check]
710 711
[/dungeon index check]",
                    _ => (byte)1);
            Check(
                "unclosed sequential section rejects only its definition",
                unclosedSectionCatalog.TryGetByGroupKey(121, out _)
                && !unclosedSectionCatalog.TryGetByGroupKey(122, out _)
                && unclosedSectionCatalog.Definitions.Count == 1,
                ref failures);
        }

        private static void VerifyEmptyEtcFailClosed(ref int failures)
        {
            var empty = SequentialDungeonDefinitionCatalog.Parse(
                string.Empty,
                _ => (byte)1);
            var whitespace = SequentialDungeonDefinitionCatalog.Parse(
                " \r\n\t",
                _ => (byte)1);
            Check(
                "empty ETC publishes no definitions",
                empty.Definitions.Count == 0
                && whitespace.Definitions.Count == 0,
                ref failures);
        }

        private static void VerifyMissingEtcLoadFailClosed(ref int failures)
        {
            const string missingPath =
                "etc/missing_sequential_dungeon_info.etc";
            var readAttempts = 0;
            var catalog = SequentialDungeonDefinitionCatalog.Load(
                missingPath,
                path =>
                {
                    readAttempts++;
                    throw new FileNotFoundException(
                        "Injected missing ETC.",
                        path);
                },
                _ => (byte)1);
            Check(
                "missing production ETC fails closed without publishing data",
                readAttempts == 1 && catalog.Definitions.Count == 0,
                ref failures);
        }

        private static string BuildValidDefinition(int groupKey) => $@"
[sequential dungeon]
{groupKey}
[dungeon index check]
700 701
[/dungeon index check]
[/sequential dungeon]";

        private static string BuildUnclosedFieldDefinition(
            int groupKey,
            string tag,
            string data)
        {
            var dungeonBlock = string.Equals(
                tag,
                "dungeon index check",
                StringComparison.Ordinal)
                    ? $@"[dungeon index check]
{data}"
                    : @"[dungeon index check]
700 701
[/dungeon index check]";
            var fieldBlock = string.Equals(
                tag,
                "dungeon index check",
                StringComparison.Ordinal)
                    ? string.Empty
                    : $@"
[{tag}]
{data}";
            return $@"
[sequential dungeon]
{groupKey}
{dungeonBlock}{fieldBlock}
[/sequential dungeon]";
        }

        private static void VerifyCurrentPvfAndInstanceFreeze(ref int failures)
        {
            var foundCurrent = SequentialDungeonDefinitionCatalog.Current
                .TryGetByGroupKey(41, out var current);
            Check(
                "current PVF publishes key 41",
                foundCurrent
                && current.IsAntonDungeonSequence
                && current.RewardableDungeonIds.Contains(247)
                && current.MonsterIds.Count == 19
                && current.ClearRewardGroups.Count == 4,
                ref failures);
            Check(
                "Anton conquest consumes the primary ETC definition",
                AntonNormalConquest.TryGetSequence(243, out var sequence)
                && current != null
                && sequence.ConfigKey == current.GroupKey
                && sequence.DungeonIds.SequenceEqual(current.DungeonIds),
                ref failures);
            Check(
                "Anton conquest keeps only DGN-tagged ETC definitions",
                AntonNormalConquest.TryGetSequenceByKey(28, out _)
                && AntonNormalConquest.TryGetSequenceByKey(41, out _)
                && !AntonNormalConquest.TryGetSequenceByKey(105, out _)
                && !AntonNormalConquest.TryGetSequence(4108, out _),
                ref failures);

            var instance = new DungeonInstance(247, 0);
            Check(
                "dungeon instance freezes primary sequential definition",
                ReferenceEquals(instance.SequentialDefinition, current),
                ref failures);

            var foundNonAnton = SequentialDungeonDefinitionCatalog.Current
                .TryGetByGroupKey(105, out var nonAnton);
            var nonAntonInstance = new DungeonInstance(4108, 0);
            Check(
                "generic catalog still freezes a non-Anton definition",
                foundNonAnton
                && !nonAnton.IsAntonDungeonSequence
                && nonAnton.DungeonIds.Contains(4108)
                && ReferenceEquals(
                    nonAntonInstance.SequentialDefinition,
                    nonAnton),
                ref failures);
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

        private static bool ThrowsArgument(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}
