using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningDailyResetSelfTest
    {
        private static readonly Dictionary<int, int> ExpectedStates =
            new Dictionary<int, int>
            {
                [10157831] = 0,
                [10157832] = 1,
                [10157833] = 2,
                [10157834] = 1,
            };

        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_DAILY_RESET selftest ===");
            var failures = 0;
            VerifyRewardPoolParser(ref failures);
            VerifyTwoStageRewardResolution(ref failures);
            VerifyMalformedRewardPoolsFailClosed(ref failures);
            VerifyRewardDrawPreservesState(ref failures);
            VerifyCurrentPvfPreparedPools(ref failures);
            VerifyRewardClaimRollback(ref failures);
            VerifyCrossDayReset(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_DAILY_RESET selftest passed."
                    : $"ANTON_AWAKENING_DAILY_RESET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRewardPoolParser(ref int failures)
        {
            const string rewards =
                "915 10157831 0 10 10157832 1 5 10157833 2 70 10157834 1";
            var definition = ParseRewardDefinition(
                BuildSequentialRewardConfig(26, 41, rewards));

            Check("key 41 parser finds four entries", definition?.ClearRewardGroups.Count == 4, ref failures);
            Check(
                "key 41 parser preserves weights",
                definition != null
                && definition.ClearRewardGroups.Select(value => value.Weight)
                    .SequenceEqual(new[] { 915, 10, 5, 70 }),
                ref failures);
            Check(
                "key 41 parser preserves item IDs",
                definition != null
                && definition.ClearRewardGroups
                    .Select(value => value.RewardGroupItemId)
                    .SequenceEqual(ExpectedStates.Keys),
                ref failures);
            Check(
                "key 41 parser preserves PVF states",
                definition != null
                && definition.ClearRewardGroups.Select(value => value.CardState)
                    .SequenceEqual(ExpectedStates.Values),
                ref failures);
        }

        private static void VerifyMalformedRewardPoolsFailClosed(ref int failures)
        {
            var malformed = new[]
            {
                BuildSequentialRewardConfig(26, 42, "915 10157831 0"),
                BuildSequentialRewardConfig(26, 41, null),
                BuildSequentialRewardConfig(26, 41, "915 10157831"),
                BuildSequentialRewardConfig(26, 41, "bad 10157831 0"),
                BuildSequentialRewardConfig(26, 41, "0 10157831 0"),
                BuildSequentialRewardConfig(26, 41, "915 0 0"),
                BuildSequentialRewardConfig(26, 41, "915 10157831 -1"),
            };

            Check(
                "malformed key 41 definitions fail closed",
                malformed.All(value =>
                    ParseRewardDefinition(value) == null),
                ref failures);
        }

        private static void VerifyTwoStageRewardResolution(ref int failures)
        {
            const string config = @"
[sequential dungeon]
99
[dungeon index check]
243 244 245 246 247
[/dungeon index check]
[rewardable dungeon index]
247
[/rewardable dungeon index]
[clear reward item]
1 7001 0 2 7002 1
[/clear reward item]
[/sequential dungeon]";
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                config,
                _ => (byte)2);
            Check(
                "two-stage fixture parses a rewardable definition",
                catalog.TryGetByGroupKey(99, out var definition)
                    && definition.RewardableDungeonIds.Contains(247),
                ref failures);

            var rolls = new Queue<int>(new[] { 1, 4, 0, 0, 1, 4, 0, 0 });
            var service = new AntonAwakeningDailyCardService(
                null,
                id => id == 7002
                    ? BuildUpgradableLegacy(
                        (90001, 4, 1),
                        (90002, 6, 3))
                    : id == 7001
                        ? BuildUpgradableLegacy((91000, 1, 2))
                        : null,
                maximum => rolls.Dequeue());
            var preparedOk = service.TryPrepareRewardPools(
                definition,
                247,
                out var prepared,
                out _);
            Check(
                "outer group and inner STK weights resolve final item/quantity",
                preparedOk
                && service.TryDrawPreparedReward(prepared, out var reward, out _)
                    && reward.GroupKey == 99
                    && reward.RewardableDungeonId == 247
                    && reward.RewardGroupItemId == 7002
                    && reward.ItemId == 90002
                    && reward.Quantity == 3
                    && reward.CardState == 1,
                ref failures);

            Check(
                "each participant consumes independent outer and inner rolls",
                service.TryDrawPreparedReward(prepared, out var second, out _)
                    && second.ItemId == 91000
                    && second.Quantity == 2
                    && rolls.Count == 4,
                ref failures);

            var malformedStk = new[]
            {
                new StackableItemFile { StackableType = "[upgradable legacy]" },
                BuildUpgradableLegacy((0, 1, 1)),
                BuildUpgradableLegacy((90001, 0, 1)),
                BuildUpgradableLegacy((90001, 1, 0)),
            };
            foreach (var stackable in malformedStk)
            {
                var malformedService = new AntonAwakeningDailyCardService(
                    null,
                    _ => stackable,
                    _ => 0);
                Check(
                    "invalid or empty STK fails closed",
                    !malformedService.TryPrepareRewardPools(
                        definition,
                        247,
                        out _,
                        out _),
                    ref failures);
            }

            var validationRollCalls = 0;
            var lateInvalidService = new AntonAwakeningDailyCardService(
                null,
                itemId => itemId == 7001
                    ? BuildUpgradableLegacy((90001, 1, 1))
                    : BuildUpgradableLegacy((90002, 1, 0)),
                _ =>
                {
                    validationRollCalls++;
                    return 0;
                });
            Check(
                "all outer STK pools are validated before the first RNG call",
                !lateInvalidService.TryPrepareRewardPools(
                    definition,
                    247,
                    out _,
                    out _)
                && validationRollCalls == 0,
                ref failures);

            var parsedInvalidCount = StackableItemFile.Parse(
                "[stackable type]\n`[upgradable legacy]`\n[/stackable type]\n"
                + "[int data]\n90001 1 0\n[/int data]");
            Check(
                "PvfLib preserves non-positive upgradable legacy count",
                parsedInvalidCount.UpgradableLegacyRewards.Count == 1
                && parsedInvalidCount.UpgradableLegacyRewards[0].Count == 0,
                ref failures);
            var parsedInvalidService = new AntonAwakeningDailyCardService(
                null,
                _ => parsedInvalidCount,
                _ => 0);
            Check(
                "parsed zero-count STK is rejected before rolling",
                !parsedInvalidService.TryPrepareRewardPools(
                    definition,
                    247,
                    out _,
                    out _),
                ref failures);

            var randomLegacy = BuildUpgradableLegacy((90001, 1, 1));
            randomLegacy.StackableType = "[random upgradable legacy]";
            var randomLegacyService = new AntonAwakeningDailyCardService(
                null,
                _ => randomLegacy,
                _ => 0);
            Check(
                "random upgradable legacy is not reinterpreted as a legacy pool",
                !randomLegacyService.TryPrepareRewardPools(
                    definition,
                    247,
                    out _,
                    out _),
                ref failures);
        }

        private static StackableItemFile BuildUpgradableLegacy(
            params (int ItemId, int Weight, int Count)[] entries)
        {
            var stackable = new StackableItemFile
            {
                StackableType = "[upgradable legacy]",
            };
            foreach (var entry in entries)
            {
                stackable.UpgradableLegacyRewards.Add(
                    new BoosterRewardEntry
                    {
                        RewardKind = "upgradable legacy",
                        ItemId = entry.ItemId,
                        Weight = entry.Weight,
                        Count = entry.Count,
                    });
            }
            return stackable;
        }

        private static void VerifyRewardDrawPreservesState(ref int failures)
        {
            var definition = ParseRewardDefinition(
                BuildSequentialRewardConfig(
                    26,
                    41,
                    "1 10157831 0 1 10157832 1 1 10157833 2 1 10157834 1"));
            var groups = definition?.ClearRewardGroups;
            for (var index = 0; index < (groups?.Count ?? 0); index++)
            {
                var rolls = new Queue<int>(new[] { index, 0 });
                var service = new AntonAwakeningDailyCardService(
                    null,
                    _ => BuildUpgradableLegacy((90000 + index, 1, index + 1)),
                    _ => rolls.Dequeue());
                var preparedOk = service.TryPrepareRewardPools(
                    definition,
                    247,
                    out var prepared,
                    out _);
                var reward = default(AntonAwakeningRewardDefinition);
                var drawn = preparedOk
                    && service.TryDrawPreparedReward(
                        prepared,
                        out reward,
                        out _);
                Check($"draw {index} succeeds", drawn, ref failures);
                Check(
                    $"draw {index} keeps item/state pair",
                    drawn
                    && reward.RewardGroupItemId
                        == groups[index].RewardGroupItemId
                    && reward.State == groups[index].CardState,
                    ref failures);
            }
        }

        private static void VerifyCurrentPvfPreparedPools(ref int failures)
        {
            var catalog = SequentialDungeonDefinitionCatalog.Current;
            var resolved = catalog.TryResolveRewardableByDungeonId(
                247,
                out var definition);
            var service = new AntonAwakeningDailyCardService(
                null,
                StackableItemProvider.Load,
                _ => 0);
            AntonAwakeningPreparedRewardPools prepared = null;
            var preparedOk = resolved
                && service.TryPrepareRewardPools(
                    definition,
                    247,
                    out prepared,
                    out _);
            Check(
                "current PVF prepares all four upgradable legacy reward groups",
                preparedOk
                && prepared.Groups.Count == 4
                && prepared.Groups.All(group =>
                    group.Entries.Count > 0
                    && group.Entries.All(entry =>
                        entry.ItemId > 0
                        && entry.Weight > 0
                        && entry.Quantity > 0)),
                ref failures);
            Check(
                "current PVF prepared pool draws a legal final reward",
                preparedOk
                && service.TryDrawPreparedReward(
                    prepared,
                    out var reward,
                    out _)
                && reward.IsValid,
                ref failures);
        }

        private static void VerifyRewardClaimRollback(ref int failures)
        {
            failures += WithDatabase(
                "claim-rollback",
                (database, dailyReset, characterId) =>
                {
                    var localFailures = 0;
                    var service = new AntonAwakeningDailyCardService(
                        dailyReset,
                        _ => null,
                        _ => 0);
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check(
                            "transactional reward claim succeeds",
                            service.TryClaimReward(
                                connection,
                                transaction,
                                characterId,
                                41,
                                247),
                            ref localFailures);
                        transaction.Rollback();
                    }
                    Check(
                        "rolled-back reward claim remains unclaimed",
                        !service.HasClaimedRewardToday(characterId, 41, 247),
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyCrossDayReset(ref int failures)
        {
            failures += WithDatabase(
                "cross-day",
                (database, dailyReset, characterId) =>
                {
                    var localFailures = 0;
                    var service = new AntonAwakeningDailyCardService(
                        dailyReset,
                        _ => null,
                        _ => 0);
                    Check("first daily reward claim succeeds", service.TryClaimReward(characterId, 41, 247), ref localFailures);
                    Check("reward reports claimed", service.HasClaimedRewardToday(characterId, 41, 247), ref localFailures);
                    var restartedService = new AntonAwakeningDailyCardService(
                        new DailyResetService(database),
                        _ => null,
                        _ => 0);
                    Check(
                        "same-day reward claim survives service restart",
                        restartedService.HasClaimedRewardToday(characterId, 41, 247),
                        ref localFailures);
                    using (var connection = database.OpenConnection())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "UPDATE character_daily_reset SET day_id = 0 WHERE character_id = @cid;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.ExecuteNonQuery();
                    }
                    var nextDayService = new AntonAwakeningDailyCardService(
                        new DailyResetService(database),
                        _ => null,
                        _ => 0);
                    Check(
                        "day rollover clears reward claim after service restart",
                        !nextDayService.HasClaimedRewardToday(characterId, 41, 247),
                        ref localFailures);
                    Check(
                        "next-day reward claim succeeds",
                        nextDayService.TryClaimReward(characterId, 41, 247),
                        ref localFailures);
                    return localFailures;
                });
        }

        private static string BuildSequentialRewardConfig(
            int firstKey,
            int targetKey,
            string rewardLine)
        {
            var rewardBlock = rewardLine == null
                ? string.Empty
                : $@"
[clear reward item]
{rewardLine}
[/clear reward item]";
            return $@"
[sequential dungeon]
{firstKey}
[dungeon index check]
243 244 245 246 247
[/dungeon index check]
[clear reward item]
1 90000000 0
[/clear reward item]
[/sequential dungeon]
[sequential dungeon]
{targetKey}
[dungeon index check]
243 244 245 246 247
[/dungeon index check]
[rewardable dungeon index]
247
[/rewardable dungeon index]{rewardBlock}
[/sequential dungeon]";
        }

        private static SequentialDungeonDefinition ParseRewardDefinition(
            string config)
        {
            try
            {
                var catalog = SequentialDungeonDefinitionCatalog.Parse(
                    config,
                    _ => (byte)2);
                if (!catalog.TryGetByGroupKey(41, out var definition)
                    || definition.ClearRewardGroups.Count == 0)
                {
                    return null;
                }

                return definition;
            }
            catch
            {
                return null;
            }
        }

        private static int WithDatabase(
            string suffix,
            Func<IGameDatabase, DailyResetService, int, int> action)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_{suffix}_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                const int accountId = 57800;
                const int characterId = 57801;
                SeedAccount(database, accountId, $"anton-{suffix}-a");
                SeedCharacter(database, characterId, accountId, $"anton-{suffix}-c");
                return action(database, new DailyResetService(database), characterId);
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void SeedAccount(IGameDatabase database, int accountId, string mid)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@mid", mid);
                command.ExecuteNonQuery();
            }
        }

        private static void SeedCharacter(
            IGameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Cleanup is best-effort because SQLite may still be releasing a handle.
            }
        }
    }
}
