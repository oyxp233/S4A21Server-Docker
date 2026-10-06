using System;
using System.IO;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class DimensionDungeonEnterTicketSelfTest
    {
        private const int CharacterId = 93021;
        private const int AccountId = 93020;
        private const int MaterialTicketItemId = 10158126;
        private const int FiveCountTicketItemId = 10158692;
        private const int MaterialItemId = 10155142;
        private const short TicketSlot = 10;
        private const short MaterialSlot = 11;
        private const short FiveCountSlot = 12;

        public static int Run()
        {
            Console.WriteLine("=== DIMENSION_ENTER_TICKET selftest ===");
            var failures = 0;
            VerifyDefinitions(ref failures);
            VerifyUseAndDailyRollover(ref failures);
            VerifyLivePvf(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "DIMENSION_ENTER_TICKET selftest passed."
                    : $"DIMENSION_ENTER_TICKET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyDefinitions(ref int failures)
        {
            Check(
                "one-count ticket resolves town-only entry",
                TryResolveSample(1, null, out var one)
                    && one.EnterCount == 1
                    && !one.RequiresMaterial
                    && one.TownOnly,
                ref failures);
            Check(
                "material ticket resolves ten dimension fragments",
                TryResolveSample(1, "10155142 10", out var material)
                    && material.EnterCount == 1
                    && material.MaterialItemTemplateId == MaterialItemId
                    && material.MaterialCount == 10
                    && material.TownOnly,
                ref failures);
            Check(
                "five-count ticket resolves five entries",
                TryResolveSample(5, null, out var five)
                    && five.EnterCount == 5
                    && !five.RequiresMaterial,
                ref failures);
            Check(
                "plain waste item is not an entry ticket",
                !DimensionDungeonEnterTicketRules.TryResolve(
                    StackableItemFile.Parse("[stackable type]\n`[waste]` 0\n"),
                    out _),
                ref failures);
        }

        private static void VerifyUseAndDailyRollover(ref int failures)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo_dimension_enter_ticket_" + Guid.NewGuid().ToString("N") + ".db");
            var sessionId = Guid.NewGuid();
            var preBoundaryUtc = new DateTime(2026, 8, 20, 21, 30, 0, DateTimeKind.Utc);
            var postBoundaryUtc = new DateTime(2026, 8, 20, 22, 30, 0, DateTimeKind.Utc);
            try
            {
                var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(database);
                var inventory = new InventoryService(CharacterId, AccountId, database);
                inventory.SetItem(
                    InventoryListType.Main,
                    TicketSlot,
                    Stack(MaterialTicketItemId, 2));
                inventory.SetItem(
                    InventoryListType.Main,
                    MaterialSlot,
                    Stack(MaterialItemId, 25));
                inventory.SetItem(
                    InventoryListType.Main,
                    FiveCountSlot,
                    Stack(FiveCountTicketItemId, 1));
                var lease = InventoryContext.Register(sessionId, inventory);
                Check(
                    "ticket fixture inventory persists",
                    InventoryPersistenceService.SaveDirty(lease),
                    ref failures);

                var runeHandled = InventoryEquipmentMutationService.TryUseEquipmentEffectRune(
                    lease.Inventory,
                    new EquipmentEffectRuneUseRequest
                    {
                        SourceListType = InventoryListType.Main,
                        SourceSlotIndex = TicketSlot,
                        ExpectedSourceItemTemplateId = 1,
                    },
                    out _);
                Check(
                    "entry ticket is not consumed as an equipment-effect rune",
                    !runeHandled && Count(lease, TicketSlot) == 2,
                    ref failures);

                var entryLimits = new DungeonEntryLimitService(database, () => preBoundaryUtc);
                var materialTicket = ParseSample(1, "10155142 10");
                var materialDefinition = Resolve(materialTicket);
                var rejected = DimensionDungeonEnterTicketService.TryUse(
                    lease,
                    InventoryListType.Main,
                    TicketSlot,
                    MaterialTicketItemId,
                    false,
                    materialTicket,
                    materialDefinition,
                    5,
                    0,
                    entryLimits);
                Check(
                    "entry ticket is rejected outside town without consumption",
                    rejected.Status == DimensionDungeonEnterTicketStatus.LocationRestricted
                        && Count(lease, TicketSlot) == 2
                        && Count(lease, MaterialSlot) == 25
                        && entryLimits.LoadDimensionGateLimit(CharacterId, 5, 0).ExtraCount == 0,
                    ref failures);

                var shortage = DimensionDungeonEnterTicketService.TryUse(
                    lease,
                    InventoryListType.Main,
                    TicketSlot,
                    MaterialTicketItemId,
                    true,
                    materialTicket,
                    new DimensionDungeonEnterTicketDefinition(1, MaterialItemId, 100, true),
                    5,
                    0,
                    entryLimits);
                Check(
                    "missing material rolls back the ticket",
                    shortage.Status == DimensionDungeonEnterTicketStatus.InsufficientMaterial
                        && Count(lease, TicketSlot) == 2
                        && Count(lease, MaterialSlot) == 25
                        && entryLimits.LoadDimensionGateLimit(CharacterId, 5, 0).ExtraCount == 0,
                    ref failures);

                var added = DimensionDungeonEnterTicketService.TryUse(
                    lease,
                    InventoryListType.Main,
                    TicketSlot,
                    MaterialTicketItemId,
                    true,
                    materialTicket,
                    materialDefinition,
                    5,
                    0,
                    entryLimits);
                var afterOne = entryLimits.LoadDimensionGateLimit(CharacterId, 5, 0);
                Check(
                    "material ticket adds one extra entry and consumes ten fragments",
                    added.Success
                        && added.RemainingCount == 5
                        && added.ExtraCount == 1
                        && afterOne.CurrentCount == 5
                        && afterOne.ExtraCount == 1
                        && afterOne.DayId == DailyResetService.TodayId(preBoundaryUtc)
                        && Count(lease, TicketSlot) == 1
                        && Count(lease, MaterialSlot) == 15,
                    ref failures);

                var fiveTicket = ParseSample(5, null);
                var fiveAdded = DimensionDungeonEnterTicketService.TryUse(
                    lease,
                    InventoryListType.Main,
                    FiveCountSlot,
                    FiveCountTicketItemId,
                    true,
                    fiveTicket,
                    Resolve(fiveTicket),
                    5,
                    0,
                    entryLimits);
                var afterFive = entryLimits.LoadDimensionGateLimit(CharacterId, 5, 0);
                Check(
                    "five-count ticket stacks onto the same day's extra count",
                    fiveAdded.Success
                        && fiveAdded.ExtraCount == 6
                        && afterFive.CurrentCount == 5
                        && afterFive.ExtraCount == 6
                        && afterFive.UsedCount == 0
                        && Count(lease, FiveCountSlot) == 0,
                    ref failures);

                var nextDay = new DungeonEntryLimitService(database, () => postBoundaryUtc);
                var rolled = nextDay.LoadDimensionGateLimit(CharacterId, 5, 0);
                Check(
                    "ticket extra count resets with the Beijing 06:00 dimension gate day",
                    rolled.CurrentCount == 5
                        && rolled.ExtraCount == 0
                        && rolled.UsedCount == 0,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var candidate = databasePath + suffix;
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            File.Delete(candidate);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static void VerifyLivePvf(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("[SKIP] live dimension enter ticket PVF (PVF_ARCHIVE_PATH not set)");
                return;
            }

            CheckLive(10158125, 1, 0, 0, ref failures);
            CheckLive(10158126, 1, MaterialItemId, 10, ref failures);
            CheckLive(10158692, 5, 0, 0, ref failures);
            CheckLive(490003748, 5, 0, 0, ref failures);
        }

        private static void CheckLive(
            int itemId,
            int enterCount,
            int materialItemId,
            int materialCount,
            ref int failures)
        {
            var stackable = StackableItemProvider.Load(itemId);
            var resolved = DimensionDungeonEnterTicketRules.TryResolve(stackable, out var definition);
            Check(
                "live PVF ticket " + itemId + " grants dimension entries",
                resolved
                    && definition.EnterCount == enterCount
                    && definition.MaterialItemTemplateId == materialItemId
                    && definition.MaterialCount == materialCount
                    && definition.TownOnly,
                ref failures);
        }

        private static bool TryResolveSample(
            int enterCount,
            string needMaterial,
            out DimensionDungeonEnterTicketDefinition definition)
        {
            return DimensionDungeonEnterTicketRules.TryResolve(
                ParseSample(enterCount, needMaterial),
                out definition);
        }

        private static DimensionDungeonEnterTicketDefinition Resolve(StackableItemFile stackable)
        {
            DimensionDungeonEnterTicketRules.TryResolve(stackable, out var definition);
            return definition;
        }

        private static StackableItemFile ParseSample(int enterCount, string needMaterial)
        {
            var material = string.IsNullOrWhiteSpace(needMaterial)
                ? string.Empty
                : "[need material]\n" + needMaterial + "\n[/need material]\n";
            return StackableItemFile.Parse(
                "[name]\n`ticket`\n" +
                "[stackable type]\n`[waste]` 0\n" +
                "[action type]\n`[add dimension dungeon enter count]` " + enterCount + "\n[/action type]\n" +
                material +
                "[action usable place]\n`[village]` `[seria room]`\n[/action usable place]\n");
        }

        private static int Count(InventoryLease lease, short slotIndex)
        {
            return lease.Inventory.GetItem(InventoryListType.Main, slotIndex)?.Count ?? 0;
        }

        private static ItemCore Stack(int itemId, int count)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindMaterial,
                ItemId = itemId,
                Count = count,
            };
        }

        private static void SeedCharacter(GameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'dimension-enter-ticket', '');
INSERT INTO characters(character_id, account_id, name, job)
VALUES(@characterId, @accountId, 'dimension-enter-ticket', 0);";
                command.Parameters.AddWithValue("@accountId", AccountId);
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine("[PASS] " + name);
                return;
            }

            failures++;
            Console.WriteLine("[FAIL] " + name);
        }
    }
}
