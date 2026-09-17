using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class LevelUpTicketSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== LEVEL_UP_TICKET selftest ===");
            var failures = 0;

            VerifyStageBoundaries(ref failures);
            VerifyEarlierStages(ref failures);
            VerifyExcludedActiveQuests(ref failures);
            VerifyMinimumLevel(ref failures);
            VerifyRollback(ref failures);

            Console.WriteLine(failures == 0
                ? "LEVEL_UP_TICKET selftest passed"
                : $"LEVEL_UP_TICKET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyStageBoundaries(ref int failures)
        {
            // The level-17 and level-19 stages both declare [level] 17 99.
            Check("PVF preserves the shared regional level and distinct stages",
                QuestCatalog.Get(1816).Level[0] == 17
                && QuestCatalog.Get(1817).Level[0] == 17
                && QuestCatalog.TryGetPath(1817, out var path)
                && path.EndsWith("epic_19_salif_1.qst", StringComparison.OrdinalIgnoreCase),
                ref failures);

            using (var fixture = new TicketFixture(17, 3, 1814, 1817))
            {
                var first = fixture.Use();
                fixture.CheckSuccess(first, 17, 2, ref failures);
                var flags = fixture.Flags();
                Check("17 to 18 completes the level-17 chain and keeps later stages",
                    flags.ContainsKey(1816)
                    && new[] { 1817, 1824, 1832, 1842 }.All(id => !flags.ContainsKey(id))
                    && !fixture.ActiveIds().Contains(1814)
                    && fixture.ActiveIds().Contains(1817),
                    ref failures);

                var second = fixture.Use();
                fixture.CheckSuccess(second, 18, 1, ref failures);
                Check("18 to 19 uses the pre-upgrade level as the cutoff",
                    second.Success
                    && second.AutoCompletedQuestIds.Count == 0
                    && !fixture.Flags().ContainsKey(1817)
                    && fixture.ActiveIds().Contains(1817),
                    ref failures);

                var third = fixture.Use();
                fixture.CheckSuccess(third, 19, 0, ref failures);
                flags = fixture.Flags();
                Check("19 to 20 completes the level-19 chain and exposes level-21 quests",
                    flags.ContainsKey(1817)
                    && flags.ContainsKey(1823)
                    && !flags.ContainsKey(1824)
                    && !fixture.ActiveIds().Contains(1817)
                    && QuestData.ComputeAcceptableQuests(
                        20, 0, 0, new HashSet<int>(flags.Keys), flags).Contains(1824),
                    ref failures);

                var repeated = fixture.Use();
                Check("an exhausted ticket slot preserves the completed quest state",
                    !repeated.Success
                    && fixture.Flags().OrderBy(pair => pair.Key)
                        .SequenceEqual(flags.OrderBy(pair => pair.Key)),
                    ref failures);
            }
        }

        private static void VerifyEarlierStages(ref int failures)
        {
            using (var fixture = new TicketFixture(18, 1))
            {
                var result = fixture.Use();
                fixture.CheckSuccess(result, 18, 0, ref failures);
                var flags = fixture.Flags();
                Check("level 18 clears unfinished earlier stages through level 17",
                    flags.ContainsKey(1777)
                    && flags.ContainsKey(1793)
                    && flags.ContainsKey(1816)
                    && !flags.ContainsKey(1817),
                    ref failures);
            }
        }

        private static void VerifyExcludedActiveQuests(ref int failures)
        {
            // 1016 has no stage in its filename; 1898 is a hidden epic_35 quest.
            using (var fixture = new TicketFixture(35, 1, 1016, 1898, 4073, 4951))
            {
                var result = fixture.Use();
                fixture.CheckSuccess(result, 35, 0, ref failures);
                var flags = fixture.Flags();
                var active = fixture.ActiveIds();
                Check("unnamed, hidden and event quests stay active and uncleared",
                    new[] { 1016, 1898, 4073, 4951 }.All(
                        id => active.Contains(id) && !flags.ContainsKey(id)),
                    ref failures);
                Check("hidden PvP quests stay outside automatic completion",
                    Enumerable.Range(4740, 6).All(id => !flags.ContainsKey(id)),
                    ref failures);
            }
        }

        private static void VerifyMinimumLevel(ref int failures)
        {
            // The Gent entry is epic_54_outer_1.qst but requires level 55.
            using (var fixture = new TicketFixture(54, 2, 2118))
            {
                var first = fixture.Use();
                fixture.CheckSuccess(first, 54, 1, ref failures);
                Check("stage 54 still respects the level-55 entry requirement",
                    !fixture.Flags().ContainsKey(2118)
                    && fixture.ActiveIds().Contains(2118),
                    ref failures);

                var second = fixture.Use();
                fixture.CheckSuccess(second, 55, 0, ref failures);
                var flags = fixture.Flags();
                Check("level 55 clears Gent entry and outskirts while keeping east gate",
                    flags.ContainsKey(2118)
                    && flags.ContainsKey(2130)
                    && !flags.ContainsKey(2131),
                    ref failures);
            }
        }

        private static void VerifyRollback(ref int failures)
        {
            using (var fixture = new TicketFixture(17, 1, 1814, 1817))
            {
                fixture.Database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
CREATE TRIGGER reject_ticket_quest BEFORE INSERT ON character_quest_completions
WHEN NEW.quest_id = 1816
BEGIN SELECT RAISE(ABORT, 'level-up ticket selftest'); END;";
                        command.ExecuteNonQuery();
                    }
                });

                var result = fixture.Use();
                Check("quest write failure rolls back ticket, level and quest changes",
                    result.Status == ExperienceItemUseStatus.PersistenceFailed
                    && fixture.Flags().Count == 0
                    && fixture.ActiveIds().SetEquals(new[] { 1814, 1817 })
                    && fixture.PersistedLevel() == 17
                    && fixture.PersistedTicketCount() == 1
                    && fixture.OnlineTicketCount() == 1,
                    ref failures);

                fixture.Database.Write((connection, transaction) =>
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DROP TRIGGER reject_ticket_quest;";
                        command.ExecuteNonQuery();
                    }
                });
                fixture.CheckSuccess(fixture.Use(), 17, 0, ref failures);
                Check("retry after rollback completes only the intended stage",
                    fixture.Flags().ContainsKey(1816)
                    && !fixture.Flags().ContainsKey(1817),
                    ref failures);
            }
        }

        private sealed class TicketFixture : IDisposable, IRentalTimeProvider
        {
            private const int AccountId = 9711901;
            private const int CharacterId = 9711902;
            private const int TicketItemId = 10004024;
            private const short TicketSlot = 16;
            private readonly Guid _sessionId = Guid.NewGuid();
            private readonly InventoryLease _lease;
            private readonly ExperienceItemUseService _service;
            private uint _now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            internal TicketFixture(byte level, int ticketCount, params ushort[] activeIds)
            {
                Database = new GameDatabase(
                    Path.Combine(Path.GetTempPath(), "s4a21-level-up-ticket-"
                        + Guid.NewGuid().ToString("N") + ".db"),
                    ServerPaths.SchemaFilePath);
                using (var connection = Database.OpenConnection())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'level-up-ticket-test', '');
INSERT INTO characters (character_id, account_id, name, job, level, exp)
VALUES (@cid, @aid, 'level-up-ticket-test', 0, @level, @exp);
INSERT INTO character_subtype0_fields (character_id) VALUES (@cid);
INSERT INTO character_subtype1_fields (character_id) VALUES (@cid);
INSERT INTO character_init_flags (character_id) VALUES (@cid);";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.Parameters.AddWithValue("@level", level);
                        command.Parameters.AddWithValue("@exp", ExpTableProvider.GetLevelThreshold(level - 1));
                        command.ExecuteNonQuery();
                    }
                    for (var slot = 0; slot < activeIds.Length; slot++)
                        QuestRepository.InsertActiveQuest(connection, null, CharacterId, slot, activeIds[slot], 1);

                    var inventory = InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database);
                    inventory.SetItem(InventoryListType.Main, TicketSlot, new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = TicketItemId,
                        Count = ticketCount,
                    });
                    _lease = InventoryContext.Register(_sessionId, CharacterId, inventory);
                }
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(_lease, "selftest-seed-level-up-ticket"))
                    throw new InvalidOperationException("failed to persist level-up ticket fixture");
                _service = new ExperienceItemUseService(Database, this);
            }

            internal GameDatabase Database { get; }
            public uint UtcNowUnixSeconds() => _now;

            internal ExperienceItemUseResult Use()
            {
                _now += 60;
                return _service.UseLevelUpTicketBySlot(CharacterId, AccountId, TicketSlot, ExperienceItemUseLocation.Town);
            }

            internal Dictionary<int, int> Flags()
                => new QuestRepository(Database.ConnectionString).LoadClearedFlags(CharacterId);

            internal HashSet<int> ActiveIds()
                => new HashSet<int>(new QuestRepository(Database.ConnectionString)
                    .LoadActiveQuests(CharacterId).Select(quest => (int)quest.QuestId));

            internal int PersistedLevel()
            {
                using (var connection = Database.OpenConnection())
                    return new SqliteCharacterProgressRepository(Database)
                        .LoadProgressSnapshot(connection, null, CharacterId).Level;
            }

            internal int PersistedTicketCount()
            {
                using (var connection = Database.OpenConnection())
                    return InventoryService.LoadFromDb(connection, CharacterId, AccountId, Database)
                        .GetItem(InventoryListType.Main, TicketSlot)?.Count ?? 0;
            }

            internal int OnlineTicketCount()
                => _lease.Inventory.GetItem(InventoryListType.Main, TicketSlot)?.Count ?? 0;

            internal void CheckSuccess(ExperienceItemUseResult result, int previousLevel, int remaining, ref int failures)
            {
                var flags = Flags();
                Check($"ticket {previousLevel} to {previousLevel + 1} persists progression and inventory ({result.Status}: {result.Detail})",
                    result.Success
                    && result.PreviousLevel == previousLevel
                    && result.NewLevel == previousLevel + 1
                    && result.NewExp == (uint)ExpTableProvider.GetLevelThreshold(previousLevel)
                    && PersistedLevel() == previousLevel + 1
                    && PersistedTicketCount() == remaining
                    && OnlineTicketCount() == remaining
                    && result.AutoCompletedQuestIds.All(id => flags.ContainsKey(id)),
                    ref failures);
            }

            public void Dispose()
            {
                InventoryContext.Unregister(_sessionId, CharacterId);
                SqliteConnection.ClearAllPools();
                File.Delete(Database.DatabasePath);
                File.Delete(Database.DatabasePath + "-wal");
                File.Delete(Database.DatabasePath + "-shm");
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
