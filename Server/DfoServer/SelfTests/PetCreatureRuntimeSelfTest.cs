using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers.Pets;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PetCreatureRuntimeSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PET_CREATURE_RUNTIME selftest ===");
            var failures = 0;

            VerifyTownPreviewAndApply(ref failures);
            VerifyRevivalPreviewAndApply(ref failures);
            VerifyNoopCommitSkipsDatabase(ref failures);
            VerifyDungeonFloorChargingAndAnchor(ref failures);
            VerifyDungeonDeathThreshold(ref failures);
            VerifyAdvanceDungeonAnchor(ref failures);
            Check(
                "failed death commit uses positive retry backoff",
                PetCreatureRuntimeService.DeathCommitRetryDelay >= TimeSpan.FromSeconds(1),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "PET_CREATURE_RUNTIME selftest passed."
                    : $"PET_CREATURE_RUNTIME selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyTownPreviewAndApply(ref int failures)
        {
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909001,
                accountId: 909000,
                creatureKey: 7001,
                stomach: 50,
                database: null);
            var detail = inventory.CreatureDetails.GetDetail(7001);
            var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

            var noopPreview = PetCreatureSatietyService.PreviewTownElapsed(
                inventory,
                start,
                start.AddSeconds(359));
            Check(
                "town preview keeps sub-threshold recovery out of dirty state",
                !noopPreview.StateChanged
                && detail.Stomach == 50
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var changedPreview = PetCreatureSatietyService.PreviewTownElapsed(
                inventory,
                start,
                start.AddSeconds(360));
            Check(
                "town preview reports threshold change without mutating detail",
                changedPreview.StateChanged
                && changedPreview.Before == 50
                && changedPreview.After == 51
                && detail.Stomach == 50
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var applied = PetCreatureSatietyService.ApplyTownElapsed(
                inventory,
                start,
                start.AddSeconds(360));
            Check(
                "town apply mutates and marks detail only after visible change",
                applied.StateChanged
                && detail.Stomach == 51
                && inventory.CreatureDetails.DirtyDetailUids.Contains(7001),
                ref failures);
        }

        private static void VerifyRevivalPreviewAndApply(ref int failures)
        {
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909011,
                accountId: 909010,
                creatureKey: 7011,
                stomach: 0,
                database: null);
            var detail = inventory.CreatureDetails.GetDetail(7011);

            var preview = PetCreatureSatietyService.PreviewRevival(inventory);
            Check(
                "revival preview does not mutate a dead creature",
                preview.Revived
                && preview.Before == 0
                && preview.After == 1
                && detail.Stomach == 0
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var applied = PetCreatureSatietyService.ReviveEquippedCreatureIfDead(inventory);
            Check(
                "revival apply marks the creature detail dirty",
                applied.Revived
                && detail.Stomach == 1
                && inventory.CreatureDetails.DirtyDetailUids.Contains(7011),
                ref failures);
        }

        private static void VerifyNoopCommitSkipsDatabase(ref int failures)
        {
            var database = new ThrowingGameDatabase();
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909021,
                accountId: 909020,
                creatureKey: 7021,
                stomach: 100,
                database);
            var sessionId = Guid.NewGuid();
            var lease = InventoryContext.Register(sessionId, inventory);
            try
            {
                var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
                var committed = PetCreatureSatietyCommitService.TryCommitTownElapsed(
                    lease,
                    start,
                    start.AddMinutes(30),
                    out var update);
                Check(
                    "no-op town recovery does not open a SQLite connection",
                    committed
                    && !update.StateChanged
                    && database.OpenConnectionCalls == 0,
                    ref failures);

                committed = PetCreatureSatietyCommitService.TryCommitDungeonElapsed(
                    lease,
                    start,
                    start,
                    out update);
                Check(
                    "no-op dungeon elapsed does not open a SQLite connection",
                    committed
                    && !update.StateChanged
                    && database.OpenConnectionCalls == 0,
                    ref failures);

                committed = PetCreatureSatietyCommitService.TryCommitDungeonDeath(
                    lease,
                    start,
                    start,
                    out update);
                Check(
                    "no-op dungeon death check does not open a SQLite connection",
                    committed
                    && !update.StateChanged
                    && database.OpenConnectionCalls == 0,
                    ref failures);

                var revivalCommitted = PetCreatureSatietyCommitService.TryCommitRevival(
                    lease,
                    out var revivalUpdate);
                Check(
                    "no-op revival does not open a SQLite connection",
                    revivalCommitted
                    && !revivalUpdate.Revived
                    && database.OpenConnectionCalls == 0,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, inventory.CharacterId);
            }
        }

        private static void VerifyAdvanceDungeonAnchor(ref int failures)
        {
            var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
            var changed = new PetCreatureSatietyUpdate(
                characterId: 1,
                creatureKey: 7061,
                before: 10,
                after: 8,
                elapsedSeconds: 80,
                satietyDelta: -2,
                changed: true,
                foodConsumeRatePercent: 50);
            Check(
                "dungeon anchor advances by consumed seconds divided by food multiplier",
                PetCreatureSatietyService.AdvanceDungeonAnchor(start, changed)
                    == start.AddSeconds(80),
                ref failures);

            var unchanged = new PetCreatureSatietyUpdate(
                characterId: 1,
                creatureKey: 7061,
                before: 10,
                after: 10,
                elapsedSeconds: 10,
                satietyDelta: 0,
                changed: false,
                foodConsumeRatePercent: 50);
            Check(
                "dungeon anchor stays put when nothing changed",
                PetCreatureSatietyService.AdvanceDungeonAnchor(start, unchanged) == start,
                ref failures);

            Check(
                "dungeon anchor keeps MinValue",
                PetCreatureSatietyService.AdvanceDungeonAnchor(DateTime.MinValue, changed)
                    == DateTime.MinValue,
                ref failures);
        }

        private static void VerifyDungeonFloorChargingAndAnchor(ref int failures)
        {
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909031,
                accountId: 909030,
                creatureKey: 7031,
                stomach: 50,
                database: null);
            var detail = inventory.CreatureDetails.GetDetail(7031);
            var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

            var preview = PetCreatureSatietyService.PreviewDungeonElapsed(
                inventory,
                start,
                start.AddSeconds(90));
            Check(
                "dungeon preview floors true stomach instead of per-flush ceiling",
                preview.StateChanged
                && preview.Before == 50
                && preview.After == 48
                && preview.ConsumedSatiety == 2
                && detail.Stomach == 50
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var applied = PetCreatureSatietyService.ApplyDungeonElapsed(
                inventory,
                start,
                start.AddSeconds(90));
            var anchor = PetCreatureSatietyService.AdvanceDungeonAnchor(start, applied);
            Check(
                "dungeon anchor advances by consumed points instead of resetting to settle time",
                applied.StateChanged
                && detail.Stomach == 48
                && inventory.CreatureDetails.DirtyDetailUids.Contains(7031)
                && anchor == start.AddSeconds(120),
                ref failures);

            var second = PetCreatureSatietyService.PreviewDungeonElapsed(
                inventory,
                anchor,
                anchor.AddSeconds(30));
            Check(
                "dungeon credit window charges remaining fraction from advanced anchor",
                second.StateChanged
                && second.Before == 48
                && second.After == 47
                && second.ConsumedSatiety == 1,
                ref failures);

            var clampInventory = CreateEquippedCreatureInventory(
                characterId: 909061,
                accountId: 909060,
                creatureKey: 7061,
                stomach: 1,
                database: null);
            var clampStart = start.AddMinutes(5);
            var noop = PetCreatureSatietyService.PreviewDungeonElapsed(
                clampInventory,
                clampStart,
                clampStart.AddSeconds(30));
            Check(
                "dungeon anchor stays put while alive minimum clamps visible satiety",
                !noop.StateChanged
                && noop.Before == 1
                && noop.After == 1
                && PetCreatureSatietyService.AdvanceDungeonAnchor(clampStart, noop)
                    == clampStart,
                ref failures);
        }

        private static void VerifyDungeonDeathThreshold(ref int failures)
        {
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909041,
                accountId: 909040,
                creatureKey: 7041,
                stomach: 1,
                database: null);
            var detail = inventory.CreatureDetails.GetDetail(7041);
            var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

            var alivePreview = PetCreatureSatietyService.PreviewDungeonDeath(
                inventory,
                start,
                start.AddSeconds(30));
            Check(
                "satiety 1 entering dungeon survives while true stomach remains",
                !alivePreview.StateChanged
                && alivePreview.Before == 1
                && alivePreview.After == 1
                && detail.Stomach == 1,
                ref failures);

            var deadPreview = PetCreatureSatietyService.PreviewDungeonDeath(
                inventory,
                start,
                start.AddSeconds(60));
            Check(
                "death triggers only when true stomach is exhausted",
                deadPreview.StateChanged
                && deadPreview.After == 0
                && deadPreview.ConsumedSatiety == 1
                && detail.Stomach == 1,
                ref failures);

            var appliedDeath = PetCreatureSatietyService.ApplyDungeonDeathIfExpired(
                inventory,
                start,
                start.AddSeconds(60));
            Check(
                "death apply zeroes satiety and marks detail dirty",
                appliedDeath.StateChanged
                && detail.Stomach == 0
                && inventory.CreatureDetails.DirtyDetailUids.Contains(7041),
                ref failures);

            var alreadyDead = PetCreatureSatietyService.PreviewDungeonDeath(
                inventory,
                start,
                start.AddSeconds(120));
            Check(
                "already dead creature death check is a no-op",
                !alreadyDead.StateChanged
                && !alreadyDead.Changed
                && alreadyDead.Before == 0
                && alreadyDead.After == 0,
                ref failures);

            var tickInventory = CreateEquippedCreatureInventory(
                characterId: 909051,
                accountId: 909050,
                creatureKey: 7051,
                stomach: 1,
                database: null);
            var tick = PetCreatureSatietyService.ApplyDungeonElapsed(
                tickInventory,
                start,
                start.AddSeconds(60));
            Check(
                "minute tick elapsed path surfaces exhausted satiety for death handling",
                tick.CreatureKey == 7051
                && tick.StateChanged
                && tick.After == 0
                && tickInventory.CreatureDetails.GetDetail(7051).Stomach == 0,
                ref failures);
        }

        private static InventoryService CreateEquippedCreatureInventory(
            int characterId,
            int accountId,
            int creatureKey,
            byte stomach,
            IGameDatabase database)
        {
            var inventory = new InventoryService(characterId, accountId, database);
            inventory.AttachItem(
                InventoryListType.Equipment,
                PetInventoryLayout.CreatureEquipSlot,
                new ItemCore
                {
                    ItemKind = ItemCore.KindCreature,
                    ItemId = 63000,
                    Value = creatureKey,
                });
            inventory.CreatureDetails.Attach(new CreatureDetail
            {
                Uid = creatureKey,
                Stomach = stomach,
                FieldAfterValue32 = 1,
            });
            inventory.ClearDirtyState();
            return inventory;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private sealed class ThrowingGameDatabase : IGameDatabase
        {
            public int OpenConnectionCalls { get; private set; }
            public string DatabasePath => string.Empty;
            public string SchemaFilePath => string.Empty;
            public string ConnectionString => "Data Source=unused.db";

            public SqliteConnection OpenConnection()
            {
                OpenConnectionCalls++;
                throw new InvalidOperationException("no-op commit opened the database");
            }

            public T Read<T>(Func<SqliteConnection, T> action) =>
                throw new NotSupportedException();

            public T Write<T>(
                Func<SqliteConnection, SqliteTransaction, T> action,
                bool immediate = true) =>
                throw new NotSupportedException();

            public void Write(
                Action<SqliteConnection, SqliteTransaction> action,
                bool immediate = true) =>
                throw new NotSupportedException();
        }
    }
}
