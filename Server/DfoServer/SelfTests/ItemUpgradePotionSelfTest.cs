using System;
using System.IO;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class ItemUpgradePotionSelfTest
    {
        private static int _failures;
        private static int _checks;
        private const int WeaponId = 31044;
        private const string LegacyType = "[expert town potion] 0";
        private const string UsualType = "[item upgrad buff usual upgrade]";
        private const string AmplifyType = "[item upgrad buff amplify upgrade]";

        public static int Run()
        {
            _failures = 0;
            _checks = 0;
            Console.WriteLine("=== ITEM_UPGRADE_POTION selftest ===");
            VerifyDefinitions();
            VerifyStateBoundaries();
            VerifyPersistedEffects();
            VerifyCommittedUse();
            VerifyUpgradeMatrix();
            Console.WriteLine($"ITEM_UPGRADE_POTION checks={_checks} failures={_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void VerifyDefinitions()
        {
            foreach (var entry in new[]
            {
                (2600025, ItemUpgradeMode.Reinforce, 1000, false),
                (2749959, ItemUpgradeMode.Reinforce, 1000, false),
                (10007343, ItemUpgradeMode.Reinforce, 10000, true),
                (490001533, ItemUpgradeMode.Reinforce, 1000, false),
                (490001534, ItemUpgradeMode.Amplify, 1000, false),
                (10004687, ItemUpgradeMode.Reinforce, 3000, false),
                (10004744, ItemUpgradeMode.Reinforce, 3000, false),
                (10006268, ItemUpgradeMode.Reinforce, 1000, false),
                (10004688, ItemUpgradeMode.Amplify, 5000, false),
                (10004745, ItemUpgradeMode.Amplify, 3000, false),
            })
            {
                var definition = StackableItemProvider.Load(entry.Item1);
                Check(ItemUpgradePotionBonus.TryGetBonus(definition, entry.Item2, out var weight, out var crystal)
                    && weight == entry.Item3 && crystal == entry.Item4, $"PVF bonus {entry.Item1}");
                var otherMode = entry.Item2 == ItemUpgradeMode.Reinforce ? ItemUpgradeMode.Amplify : ItemUpgradeMode.Reinforce;
                Check(!ItemUpgradePotionBonus.TryGetBonus(definition, otherMode, out _, out _),
                    $"mode isolation {entry.Item1}");
            }

            // 赛丽亚的幸运强化药剂: random event buff text is not a fixed probability.
            var randomBuff = StackableItemProvider.Load(10008220);
            Check(randomBuff != null
                && !ItemUpgradePotionBonus.TryGetBonus(randomBuff, ItemUpgradeMode.Reinforce, out _, out _),
                "random event buff potion excluded 10008220");

            // Typed definitions read the trailing integer, independent of item id.
            CheckSyntheticBonus(UsualType + " 3000", "使用后， 可增加强化成功几率， 持续3分钟。",
                ItemUpgradeMode.Reinforce, 3000, false, "typed usual 3000");
            CheckSyntheticBonus(AmplifyType + " 5000", "使用后， 可增加增幅成功几率， 持续3分钟。",
                ItemUpgradeMode.Amplify, 5000, false, "typed amplify 5000");
            CheckSyntheticBonus(UsualType + " 3000", "使用后， 可增加增幅成功几率， 持续3分钟。",
                ItemUpgradeMode.Amplify, 0, false, "typed usual excluded from amplify", expectedMatch: false);

            foreach (var amount in new[] { "", "0", "-1", "100001", "2147483648", "1.5", "1000 1000", "abc" })
                CheckSyntheticBonus(UsualType + " " + amount, "使用后， 可增加强化成功几率， 持续3分钟。",
                    ItemUpgradeMode.Reinforce, 0, false, "invalid typed bonus rejected: " + amount, expectedMatch: false);

            // Legacy `[expert town potion]`: no numeric field in PVF.
            CheckSyntheticBonus(LegacyType, "使用后， 可以提升强化成功率， 效果持续30分钟。",
                ItemUpgradeMode.Reinforce, 1000, false, "legacy default 1 percentage point");
            CheckSyntheticBonus(LegacyType, "使用后， 强化成功率增加10%%， 效果持续30秒。",
                ItemUpgradeMode.Reinforce, 10000, true, "legacy explicit 10%%");
            CheckSyntheticBonus(LegacyType, "使用后， 强化成功率增加10%， 效果持续30秒。",
                ItemUpgradeMode.Reinforce, 10000, true, "legacy explicit 10%");
            CheckSyntheticBonus(LegacyType, "使用后， 强化成功率增加 5 %% ， 效果持续30分钟。",
                ItemUpgradeMode.Reinforce, 5000, true, "legacy explicit spaced percentage");
            CheckSyntheticBonus(LegacyType, "使用后， 强化成功率增加1%%。",
                ItemUpgradeMode.Reinforce, 1000, true, "legacy explicit lower bound 1%%");
            CheckSyntheticBonus(LegacyType, "使用后， 强化成功率增加100%%。",
                ItemUpgradeMode.Reinforce, 100000, true, "legacy explicit upper bound 100%%");
            CheckSyntheticBonus(LegacyType, "使用后， 强化成功率增加10%%。",
                ItemUpgradeMode.Amplify, 0, false, "legacy potion excluded from amplify", expectedMatch: false);

            foreach (var explain in new[]
            {
                "使用后， 强化成功率增加0%%。",
                "使用后， 强化成功率增加101%%。",
                "使用后， 强化成功率增加-10%%。",
                "使用后， 强化成功率增加10.5%%。",
                "使用后， 强化成功率增加10%%， 并额外增加20%%。",
                "使用后， 强化成功率增加10%%， 冷却时间60%。",
                "使用后， 强化成功率增加十%%。",
                "使用后， 强化成功率增加%%。",
                "使用后， 强化成功率增加， 效果持续30分钟。",
                "使用后， 提高强化成功率10%%。",
                "使用后， 获得强化成功率提升Buff， 效果持续10分钟。",
                "使用后， 可以提升强化成功率， 但强化成功率增加的效果随机。",
                "使用后， 恢复30点疲劳值。",
                "效果持续30分钟。",
                "",
            })
                CheckSyntheticBonus(LegacyType, explain, ItemUpgradeMode.Reinforce, 0, false,
                    "legacy rejected: " + explain, expectedMatch: false);

            Check(!ItemUpgradePotionBonus.TryGetBonus(null, ItemUpgradeMode.Reinforce, out _, out _), "null definition");
            CheckSyntheticBonus(LegacyType, "使用后， 可以提升强化成功率， 效果持续30分钟。",
                ItemUpgradeMode.Reinforce, 0, false, "legacy without effect maintenance",
                expectedMatch: false, effectMaintenance: false);
            CheckSyntheticBonus(LegacyType, "使用后， 可以提升强化成功率， 效果持续30分钟。",
                ItemUpgradeMode.Reinforce, 0, false, "legacy without duration",
                expectedMatch: false, duration: -1);
            Check(ItemUpgradePotionBonus.TryGetBonus(StackableItemProvider.Load(2600025),
                ItemUpgradeMode.Reinforce, out var legacyWeight, out _) && legacyWeight == 1000,
                "legacy definition resolves");
        }

        private static void CheckSyntheticBonus(
            string stackableType,
            string explain,
            ItemUpgradeMode mode,
            int expectedWeight,
            bool expectedCrystal,
            string name,
            bool expectedMatch = true,
            bool effectMaintenance = true,
            int duration = 1800000)
        {
            var content = "[stackable type]\n`" + stackableType + "`\n[explain]\n`" + explain + "`\n"
                + (effectMaintenance ? "[effect maintenance]\n1 1 1 0\n" : "")
                + (duration >= 0 ? "[stat change duration]\n" + duration + " `myself`\n" : "");
            var definition = StackableItemFile.Parse(content);
            var matched = ItemUpgradePotionBonus.TryGetBonus(definition, mode, out var weight, out var crystal);
            Check(expectedMatch
                ? matched && weight == expectedWeight && crystal == expectedCrystal
                : !matched, name, quiet: true);
        }

        private static void VerifyStateBoundaries()
        {
            const int now = 1800000000;
            foreach (var id in new[] { 2600025, 2749959, 10007343, 490001533, 490001534, 10004687 })
            {
                var inventory = new InventoryService(99101, 99102);
                var definition = StackableItemProvider.Load(id);
                var mode = id == 490001534 ? ItemUpgradeMode.Amplify : ItemUpgradeMode.Reinforce;
                var duration = definition.StatChangeDurationMilliseconds / 1000;
                var expected = id == 10007343 ? 10000 : id == 10004687 ? 3000 : 1000;
                inventory.SetItem(InventoryListType.Main, 30, Stack(id, 3));
                Check(Bonus(inventory, mode, now) == 0, "inventory alone has no bonus " + id);
                inventory.ItemStates.Upsert(ItemStateKinds.Cooltime, id, now + duration);
                Check(Bonus(inventory, mode, now) == 0, "cooldown alone has no bonus " + id);
                inventory.ItemStates.Remove(ItemStateKinds.Cooltime, id);

                var plan = InventoryItemLifecycleService.PrepareUse(inventory, InventoryListType.Main, 30, id, now);
                Check(plan.Success && plan.EffectExpireTime == now + duration, "PVF use duration " + id);
                Check(InventoryDeleteService.TryUseStackableForClient(inventory, InventoryListType.Main, 30, id, out _), "consume " + id);
                InventoryItemLifecycleService.ApplyUseSuccess(inventory, plan);
                Check(inventory.GetItem(InventoryListType.Main, 30).Count == 2
                    && Bonus(inventory, mode, now) == expected, "consumed effect increases probability " + id);
                Check(Bonus(inventory, mode, now + duration - 1) == expected
                    && Bonus(inventory, mode, now + duration) == 0
                    && Bonus(inventory, mode, now + duration + 1) == 0, "exact expiration boundary " + id);
                Check(!InventoryItemLifecycleService.PrepareUse(inventory, InventoryListType.Main, 30, id, now + 1).Success,
                    "duplicate use blocked " + id);
                Check(Bonus(new InventoryService(99103, 99104), mode, now) == 0, "other character isolated " + id);
                Check(ItemUpgradePotionBonus.ResolveActiveAddWeight(inventory, mode, ItemUpgradeScene.Portable, now) == expected
                    && ItemUpgradePotionBonus.ResolveActiveAddWeight(inventory, mode, ItemUpgradeScene.Ticket, now) == 0
                    && ItemUpgradePotionBonus.ResolveActiveAddWeight(inventory, mode, ItemUpgradeScene.LuckyDeed, now) == 0,
                    "portable/ticket/lucky scene rules " + id);
                InventoryItemLifecycleService.RollbackUseSuccess(inventory, plan);
                Check(Bonus(inventory, mode, now) == 0, "rolled back effect has no bonus " + id);
            }

            var combined = new InventoryService(99105, 99106);
            foreach (var id in new[]
            {
                2600025, 2749959, 490001533, 10007343, 490001534,
                10004687, 10004744, 10006268, 10004688, 10004745,
            })
                combined.ItemStates.Upsert(ItemStateKinds.Effect, id, now + 300);
            Check(Bonus(combined, ItemUpgradeMode.Reinforce, now) == 13000, "same-family reinforce max plus crystal");
            Check(Bonus(combined, ItemUpgradeMode.Amplify, now) == 5000, "amplify takes strongest active definition");
            combined.ItemStates.Upsert(ItemStateKinds.Effect, 10004688, now);
            Check(Bonus(combined, ItemUpgradeMode.Amplify, now) == 3000, "expired strongest falls back to active weaker potion");
        }

        private static void VerifyPersistedEffects()
        {
            const int now = 1800000000;
            var databasePath = CreateTempDatabasePath("state");
            try
            {
                var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
                SeedOwner(database, 99112, 99111, "potion-test");
                using var connection = database.OpenConnection();
                var inventory = new InventoryService(99111, 99112);
                inventory.ItemStates.Upsert(ItemStateKinds.Effect, 10007343, now + 300);
                inventory.ItemStates.Upsert(ItemStateKinds.Effect, 490001534, now + 1800);
                using (var transaction = connection.BeginTransaction())
                {
                    CharacterItemStateRepository.SaveAll(connection, transaction, 99111, inventory.ItemStates);
                    transaction.Commit();
                }
                var reloaded = new InventoryService(99111, 99112);
                CharacterItemStateRepository.LoadInto(connection, 99111, reloaded.ItemStates);
                Check(Bonus(reloaded, ItemUpgradeMode.Reinforce, now) == 10000
                    && Bonus(reloaded, ItemUpgradeMode.Amplify, now) == 1000, "persisted/reloaded effects retain mode-specific bonus");
                Check(Bonus(reloaded, ItemUpgradeMode.Reinforce, now + 300) == 0
                    && Bonus(reloaded, ItemUpgradeMode.Amplify, now + 1800) == 0, "reloaded absolute deadlines still expire");
                inventory.ItemStates.Remove(ItemStateKinds.Effect, 10007343);
                using (var transaction = connection.BeginTransaction())
                {
                    CharacterItemStateRepository.SaveAll(connection, transaction, 99111, inventory.ItemStates);
                    transaction.Rollback();
                }
                var afterRollback = new InventoryService(99111, 99112);
                CharacterItemStateRepository.LoadInto(connection, 99111, afterRollback.ItemStates);
                Check(Bonus(afterRollback, ItemUpgradeMode.Reinforce, now) == 10000, "failed state transaction preserves previous effect");
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyCommittedUse()
        {
            var databasePath = CreateTempDatabasePath("commit");
            try
            {
                var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
                var characterId = 99200;
                foreach (var id in new[] { 2600025, 2749959, 10007343, 490001533, 490001534 })
                {
                    characterId++;
                    var accountId = characterId + 100;
                    SeedOwner(database, accountId, characterId, "potion-" + id);
                    var session = Guid.NewGuid();
                    var lease = LoadAndRegister(database, session, accountId, characterId);
                    try
                    {
                        lease.Inventory.SetItem(InventoryListType.Main, 30, Stack(id, 3));
                        InventoryPersistenceService.SaveDirty(lease);
                        var mode = id == 490001534 ? ItemUpgradeMode.Amplify : ItemUpgradeMode.Reinforce;
                        var expected = id == 10007343 ? 10000 : 1000;
                        var now = InventoryItemLifecycleService.UtcNowUnixSeconds();

                        var rejected = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "potion-test-reject", (connection, transaction) =>
                        {
                            var plan = InventoryItemLifecycleService.PrepareUse(lease.Inventory, InventoryListType.Main, 30, id, now);
                            InventoryDeleteService.TryUseStackableForClient(lease.Inventory, InventoryListType.Main, 30, id, out _);
                            InventoryItemLifecycleService.ApplyUseSuccess(lease.Inventory, plan);
                            return false;
                        });
                        Check(!rejected && lease.Inventory.GetItem(InventoryListType.Main, 30).Count == 3
                            && Bonus(lease.Inventory, mode, now) == 0, "failed commit restores count and effect " + id);

                        var result = InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 30, id);
                        Check(result != null && result.Consumed && lease.Inventory.GetItem(InventoryListType.Main, 30).Count == 2
                            && Bonus(lease.Inventory, mode, now) == expected, "real committed use " + id);
                        var duplicate = InventoryDeleteCommitService.TryCommitStackableUseDetailed(lease, InventoryListType.Main, 30, id);
                        Check((duplicate == null || !duplicate.Consumed) && lease.Inventory.GetItem(InventoryListType.Main, 30).Count == 2,
                            "duplicate commit consumes no extra item " + id);

                        ReleaseLease(session, characterId, lease);
                        var previousLease = lease;
                        session = Guid.NewGuid();
                        lease = LoadAndRegister(database, session, accountId, characterId);
                        Check(lease.Inventory.GetItem(InventoryListType.Main, 30).Count == 2
                            && Bonus(lease.Inventory, mode, now) == expected, "relogin retains committed count and bonus " + id);
                        Check(!OnlineInventoryMutationCommitCoordinator.TryCommit(previousLease, "potion-stale-lease", (connection, transaction) => true),
                            "stale session cannot commit " + id);
                    }
                    finally
                    {
                        ReleaseLease(session, characterId, lease);
                    }
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyUpgradeMatrix()
        {
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var potionIds = new[] { 2600025, 2749959, 490001533, 10007343, 490001534 };
            var titleWeight = ItemMetadataResolver.TryLoadEquipmentFile(26699, out var titleDefinition)
                ? Math.Max(0, titleDefinition.UpgradeProbabilityIncrease)
                : 0;
            var matrixChecks = 0;
            foreach (var kind in new[] { ItemUpgradeTableKind.Normal, ItemUpgradeTableKind.Advanced, ItemUpgradeTableKind.Amplify })
            foreach (var level in new[] { 0, 4, 9, 10, 11, 18, 30 })
            foreach (var mask in new[] { 0, 1, 2, 4, 8, 16, 7, 15, 31 })
            foreach (var title in new[] { false, true })
            {
                var mode = kind == ItemUpgradeTableKind.Amplify ? ItemUpgradeMode.Amplify : ItemUpgradeMode.Reinforce;
                var inventory = CreateUpgradeInventory(kind, level, title, out var command);
                for (var i = 0; i < potionIds.Length; i++)
                    if ((mask & (1 << i)) != 0)
                        inventory.ItemStates.Upsert(ItemStateKinds.Effect, potionIds[i], now + 1800);
                var potionWeight = mode == ItemUpgradeMode.Amplify ? ((mask & 16) != 0 ? 1000 : 0)
                    : ((mask & 7) != 0 ? 1000 : 0) + ((mask & 8) != 0 ? 10000 : 0);
                var row = ItemUpgradeTableProvider.GetTable(kind).GetRowByTargetLevel(level + 1);
                var expected = Math.Min(100000, row.DerivedSuccessWeight + potionWeight + (title ? titleWeight : 0));
                Check(InventoryItemUpgradeService.TryUpgradeItem(inventory, command, out var result)
                    && result.Success && result.FinalSuccessWeight == expected,
                    $"actual upgrade {kind} +{level}, mask={mask}, title={title}", quiet: true);
                matrixChecks++;
            }
            Console.WriteLine($"Actual NPC upgrade matrix: {matrixChecks} cases");

            // The existing portable item multiplier must still apply after the additive bonuses.
            foreach (var entry in new[] { (10007838, ItemUpgradeTableKind.Normal), (10007839, ItemUpgradeTableKind.Amplify) })
            {
                var inventory = CreateUpgradeInventory(entry.Item2, 10, true, out var command);
                inventory.SetItem(InventoryListType.Main, 20, Stack(entry.Item1, 2));
                inventory.ItemStates.Upsert(ItemStateKinds.Effect, entry.Item2 == ItemUpgradeTableKind.Normal ? 10007343 : 490001534, now + 300);
                var configOk = ItemUpgradeConsumableResolver.TryResolve(entry.Item1, StackableItemProvider.Load(entry.Item1), out var config);
                Check(configOk && config.Scene == ItemUpgradeScene.Portable, "portable PVF definition " + entry.Item1);
                if (!configOk) continue;
                var row = ItemUpgradeTableProvider.GetTable(entry.Item2).GetRowByTargetLevel(11);
                var add = entry.Item2 == ItemUpgradeTableKind.Normal ? 10000 : 1000;
                var expected = (int)Math.Min(100000, (long)Math.Min(100000, row.DerivedSuccessWeight + titleWeight + add)
                    * (100000 + config.SuccessRateBonusWeight) / 100000);
                Check(InventoryItemUpgradeService.TryUpgradeItem(inventory, command, out var result)
                    && result.Scene == ItemUpgradeScene.Portable && result.FinalSuccessWeight == expected,
                    "portable multiplier preserved " + entry.Item1);
            }
        }

        private static InventoryService CreateUpgradeInventory(ItemUpgradeTableKind kind, int level, bool title, out ItemUpgradeCommand command)
        {
            var inventory = new InventoryService(99121, 99122);
            var metadata = ItemMetadataResolver.Resolve(WeaponId);
            var target = new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = WeaponId, Value = 1,
                Upgrade = (byte)level, Durability = (ushort)metadata.Durability };
            if (kind == ItemUpgradeTableKind.Amplify) { target.AmplifyType = 1; target.AmplifyValue = 1; }
            inventory.SetItem(InventoryListType.Main, 10, target);
            inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 2000000000);
            var row = ItemUpgradeTableProvider.GetTable(kind).GetRowByTargetLevel(level + 1);
            inventory.SetItem(InventoryListType.Main, 20, Stack(row.MaterialItemId, 100000));
            if (title)
                inventory.SetItem(InventoryListType.Equipment, (short)EquipmentType.TitleName,
                    new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 26699, Value = 1 });
            command = new ItemUpgradeCommand { TargetSlotIndex = 10, TargetItemTemplateId = WeaponId, MaterialSlotIndex = 20,
                Mode = kind == ItemUpgradeTableKind.Amplify ? ItemUpgradeMode.Amplify : ItemUpgradeMode.Reinforce,
                Method = kind == ItemUpgradeTableKind.Amplify ? ItemUpgradeMethod.Amplify
                    : kind == ItemUpgradeTableKind.Advanced ? ItemUpgradeMethod.AdvancedReinforce : ItemUpgradeMethod.Reinforce };
            return inventory;
        }

        private static void SeedOwner(GameDatabase database, int accountId, int characterId, string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @name, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static InventoryLease LoadAndRegister(
            GameDatabase database,
            Guid sessionId,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(sessionId, characterId, inventory);
            }
        }

        private static void ReleaseLease(Guid sessionId, int characterId, InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryPersistenceService.SaveDirty(lease);
            InventoryContext.Unregister(sessionId, characterId);
        }

        private static string CreateTempDatabasePath(string hint) =>
            Path.Combine(Path.GetTempPath(), $"dfo_item_upgrade_potion_{hint}_{Guid.NewGuid():N}.db");

        private static void DeleteTempDatabase(string path)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                DeleteFile(path);
                DeleteFile(path + "-wal");
                DeleteFile(path + "-shm");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WARN] temp db cleanup failed: " + ex.Message);
            }
        }

        private static void DeleteFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }

        private static ItemCore Stack(int id, int count) => new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = id, Count = count };
        private static int Bonus(InventoryService inventory, ItemUpgradeMode mode, long now) =>
            ItemUpgradePotionBonus.ResolveActiveAddWeight(inventory, mode, ItemUpgradeScene.Npc, now);
        private static void Check(bool success, string name, bool quiet = false)
        {
            _checks++;
            if (!success) { _failures++; Console.WriteLine("[FAIL] " + name); }
            else if (!quiet) Console.WriteLine("[PASS] " + name);
        }
    }
}
