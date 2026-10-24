using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    // 龙跃武器([equipment grow])成长值：
    //   PVF 解析 -> 击杀累积 -> 跨列表移动(背包<->穿戴)后随物品实例迁移。
    public static class EquipmentGrowthSelfTest
    {
        private const int TestAccountId = 55101;
        private const int TestCharacterId = 55102;
        // 迁移场景用独立角色，避免与累积场景共用同一个背包槽位的成长值。
        private const int MigrationAccountId = 55201;
        private const int MigrationCharacterId = 55202;
        // 删除场景同样需要独立角色，否则前面的成长值会干扰断言。
        private const int DeleteAccountId = 55301;
        private const int DeleteCharacterId = 55302;
        // 交换场景用独立角色，避免与"空槽落位"的迁移场景互相污染。
        private const int SwapAccountId = 55401;
        private const int SwapCharacterId = 55402;
        // 普通装备样本：用于验证交换时只有龙跃武器携带成长值。
        // 不能用 101010188，那同样是龙跃武器(2星)，会一并累积成长值。
        private const int PlainWeaponItemId = 27850;
        private const short MainWeaponSlot = 9;
        private const short WeaponSlot = (short)EquipmentType.Weapon;
        private const byte ActorJob = 0;

        public static int Run()
        {
            Console.WriteLine("=== EQUIPMENT_GROWTH selftest ===");
            var failures = 0;

            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("equipment growth checks skipped: PVF_ARCHIVE_PATH is not set");
                return 0;
            }

            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_equipment_growth_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedAccountAndCharacter(database);
                VerifySchemaMigration(database, ref failures);

                var growthItemId = ResolveGrowthWeaponItemId();
                if (growthItemId <= 0)
                {
                    Check("PVF 提供带 [equipment grow] 的龙跃武器", false, ref failures);
                }
                else
                {
                    VerifyPvfDefinition(growthItemId, ref failures);
                    VerifyAccumulate(database, growthItemId, ref failures);
                    VerifyMigrationOnEquip(database, growthItemId, ref failures);
                    VerifyDeleteClearsGrowth(database, growthItemId, ref failures);
                    VerifySwapKeepsGrowth(database, growthItemId, ref failures);
                    VerifyGrowthProjection(growthItemId, ref failures);
                    VerifyAutoUpgradeStage(database, growthItemId, ref failures);
                }
            }
            finally
            {
                try { File.Delete(tempDbPath); } catch { }
            }

            Console.WriteLine(failures == 0
                ? "EQUIPMENT_GROWTH selftest passed"
                : $"EQUIPMENT_GROWTH selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // 新库直接是当前版本；既有 v37 库升级后才有成长值表。
        private static void VerifySchemaMigration(
            GameDatabase database,
            ref int failures)
        {
            using (var connection = database.OpenConnection())
            {
                Check(
                    "新库创建 character_equipment_growth 且为当前 schema 版本",
                    TableExists(connection, "character_equipment_growth")
                    && Sqlite.SqliteMigrations.ReadVersion(connection)
                        == Sqlite.SqliteMigrations.CurrentVersion,
                    ref failures);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DROP TABLE character_equipment_growth;
UPDATE schema_metadata SET schema_version = 37 WHERE singleton_id = 1;
PRAGMA user_version = 37;";
                    command.ExecuteNonQuery();
                }

                Sqlite.SqliteMigrations.Apply(connection);
                Check(
                    "v37 既有库升级到当前版本并建立成长值表",
                    Sqlite.SqliteMigrations.ReadVersion(connection)
                        == Sqlite.SqliteMigrations.CurrentVersion
                    && TableExists(connection, "character_equipment_growth"),
                    ref failures);
            }
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt64(command.ExecuteScalar()) > 0;
            }
        }

        // 找一个真实带 [equipment grow] 的武器：PVF 里这类武器的 [equipment grow] 形如 "1 N"。
        private static int ResolveGrowthWeaponItemId()
        {
            if (!ItemMetadataResolver.TryLoadEquipmentFile(101010187, out var sample)
                || sample?.Grow == null
                || !sample.Grow.HasValue)
            {
                return 0;
            }

            return 101010187;
        }

        private static void VerifyPvfDefinition(int itemId, ref int failures)
        {
            Check(
                "PVF 龙跃武器解析出 [equipment grow] 类型与星级上限",
                EquipmentGrowthService.TryResolveGrowthDefinition(itemId, out var stageLimit)
                && stageLimit >= 1
                && stageLimit <= 7,
                ref failures);

            Check(
                "普通装备不是龙跃武器",
                !EquipmentGrowthService.TryResolveGrowthDefinition(27850, out _),
                ref failures);
        }

        // 击杀累积：连续两次累积后成长值应当翻倍，且只影响龙跃武器。
        private static void VerifyAccumulate(
            GameDatabase database,
            int growthItemId,
            ref int failures)
        {
            var inventory = LoadInventory(database);
            var core = ItemCore.Create(ItemCore.KindEquipment, growthItemId);
            inventory.SetItem(InventoryListType.Main, MainWeaponSlot, core);
            inventory.SetItem(InventoryListType.Main, MainWeaponSlot + 1,
                ItemCore.Create(ItemCore.KindEquipment, 27850));

            var gain = EquipmentGrowthService.CalculateGrowthGain(50, 1, 0);
            Check(
                "成长值增量随怪物等级与副本难度上升",
                gain > 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 2, 0) > gain
                && EquipmentGrowthService.CalculateGrowthGain(60, 1, 0) > gain,
                ref failures);

            Check(
                "非怪物 actor 不产生成长值",
                EquipmentGrowthService.CalculateGrowthGain(50, 1, -1) == 0
                && EquipmentGrowthService.CalculateGrowthGain(0, 1, 0) == 0,
                ref failures);

            // 武器描述："在亡者峡谷中无效"。亡者峡谷是区域，包含绝望之塔、
            // 极限祭坛、死亡之塔、无尽祭坛、迷惘之塔等，PVF 里落在
            // Towers/ 与 AdvanceAltar/ 目录，另有 CastleoftheDead(190-195)。
            Check(
                "亡者峡谷(CastleoftheDead 190-195)不产生成长值",
                EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 190) == 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 195) == 0,
                ref failures);

            Check(
                "亡者峡谷塔类副本(Towers/ 11000 死亡之塔、11001 幻影塔)不产生成长值",
                EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 11000) == 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 11001) == 0,
                ref failures);

            Check(
                "亡者峡谷绝望之塔/无尽祭坛不产生成长值",
                EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 11008) == 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 11009) == 0,
                ref failures);

            Check(
                "亡者峡谷极限祭坛(AdvanceAltar 3511-3523)不产生成长值",
                EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 3511) == 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 3523) == 0,
                ref failures);

            Check(
                "普通副本照常产生成长值",
                EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 101) > 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 189) > 0
                && EquipmentGrowthService.CalculateGrowthGain(50, 1, 0, 196) > 0,
                ref failures);

            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var affected = EquipmentGrowthService.AccumulateForEquippedWeapons(
                    inventory,
                    connection,
                    transaction,
                    gain);
                Check(
                    "只对龙跃武器累积成长值",
                    affected == 1,
                    ref failures);
                transaction.Commit();
            }

            var first = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
            Check(
                "首次累积写入成长值",
                first != null && first.GrowthValue == gain && first.Stage == 1,
                ref failures);

            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                EquipmentGrowthService.AccumulateForEquippedWeapons(
                    inventory,
                    connection,
                    transaction,
                    gain);
                transaction.Commit();
            }

            var second = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
            Check(
                "再次击杀继续累加成长值",
                second != null && second.GrowthValue == gain * 2,
                ref failures);
        }

        // 跨列表移动(背包->穿戴)后 item_uid 变化，成长值必须跟着同一件武器迁移。
        private static void VerifyMigrationOnEquip(
            GameDatabase database,
            int growthItemId,
            ref int failures)
        {
            var inventory = LoadInventory(database, MigrationCharacterId, MigrationAccountId);
            var core = ItemCore.Create(ItemCore.KindEquipment, growthItemId);
            inventory.SetItem(InventoryListType.Main, MainWeaponSlot, core);
            var lease = CreateLease(inventory);
            try
            {
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    EquipmentGrowthService.AccumulateForEquippedWeapons(
                        inventory,
                        connection,
                        transaction,
                        7);
                    transaction.Commit();
                }

                var before = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
                Check(
                    "穿戴前成长值已落库",
                    before != null && before.GrowthValue == 7,
                    ref failures);
                if (before == null)
                    return;

                var moved = TryMove(
                    inventory,
                    InventoryListType.Main,
                    MainWeaponSlot,
                    InventoryListType.Equipment,
                    WeaponSlot,
                    out _);
                Check(
                    "龙跃武器可以从背包移动到穿戴栏",
                    moved,
                    ref failures);
                if (!moved)
                    return;

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    InventoryPersistenceService.SaveDirtyInTransaction(
                        connection,
                        transaction,
                        lease);
                    transaction.Commit();
                }

                var after = ReadGrowth(database, inventory, InventoryListType.Equipment, WeaponSlot);
                Check(
                    "跨列表移动后成长值跟随同一件武器",
                    after != null && after.GrowthValue == 7,
                    ref failures);

                var stale = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
                Check(
                    "旧槽位不再保留成长值行",
                    stale == null,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(lease.SessionId, inventory.CharacterId);
            }
        }

        // 穿戴槽已有别的装备时，把龙跃武器换上去会走 ApplySwap：
        // 两件物品互换槽位，成长值必须跟着龙跃武器走到装备槽，
        // 不能因为方向登记反了而丢失或跟错物品。
        private static void VerifySwapKeepsGrowth(
            GameDatabase database,
            int growthItemId,
            ref int failures)
        {
            var inventory = LoadInventory(database, SwapCharacterId, SwapAccountId);

            // 装备槽先放一件普通装备（无成长值），背包放龙跃武器。
            var plainCore = ItemCore.Create(ItemCore.KindEquipment, PlainWeaponItemId);
            inventory.SetItem(InventoryListType.Equipment, WeaponSlot, plainCore);
            var growthCore = ItemCore.Create(ItemCore.KindEquipment, growthItemId);
            inventory.SetItem(InventoryListType.Main, MainWeaponSlot, growthCore);

            var lease = CreateLease(inventory);
            try
            {
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    EquipmentGrowthService.AccumulateForEquippedWeapons(
                        inventory,
                        connection,
                        transaction,
                        9);
                    transaction.Commit();
                }

                var before = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
                Check(
                    "交换场景：背包里的成长值已落库",
                    before != null && before.GrowthValue == 9,
                    ref failures);
                if (before == null)
                    return;

                var moved = TryMove(
                    inventory,
                    InventoryListType.Main,
                    MainWeaponSlot,
                    InventoryListType.Equipment,
                    WeaponSlot,
                    out _);
                Check(
                    "交换场景：龙跃武器可以换到装备槽",
                    moved,
                    ref failures);
                if (!moved)
                    return;

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    InventoryPersistenceService.SaveDirtyInTransaction(
                        connection,
                        transaction,
                        lease);
                    transaction.Commit();
                }

                var after = ReadGrowth(database, inventory, InventoryListType.Equipment, WeaponSlot);
                Check(
                    "交换后成长值跟到装备槽",
                    after != null && after.GrowthValue == 9,
                    ref failures);

                var plainLeftover = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
                Check(
                    "交换后普通装备不携带成长值",
                    plainLeftover == null,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(lease.SessionId, inventory.CharacterId);
            }
        }

        // 成长值要投影到 ItemCore 的线上字段，客户端 UI 才能显示 "N星 X%" 与 "蓄能值 N/30"。
        // 分母来自 PVF etc/equipment_grow.etc [exp base]：1星1794 2星3174 3星4830 ...
        // 下列期望值全部来自实机探测并与该表逐项吻合。
        private static void VerifyGrowthProjection(int growthItemId, ref int failures)
        {
            var core = ItemCore.Create(ItemCore.KindEquipment, growthItemId);

            var need1 = EquipmentGrowthService.GetRequiredExp(1);
            Check(
                "PVF 给出 1 星成长需求 1794",
                need1 == 1794,
                ref failures);

            var need3 = EquipmentGrowthService.GetRequiredExp(3);
            Check(
                "PVF 给出 3 星成长需求 4830",
                need3 == 4830,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 0, 1);
            Check(
                "成长值 0 时进度与蓄能值都为 0",
                core.TailUnknown0 == 0 && core.TailUnknown1 == 0,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 909, 1);
            Check(
                "1星成长值 909 投影为 50% 进度",
                core.TailUnknown0 == 909
                    && core.TailUnknown0 * 100 / need1 == 50,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 1000, 1);
            Check(
                "1星成长值 1000 投影为 55% 进度",
                core.TailUnknown0 * 100 / need1 == 55,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 1111, 1);
            Check(
                "1星成长值 1111 投影为 61% 进度",
                core.TailUnknown0 * 100 / need1 == 61,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 1794, 1);
            Check(
                "1星满需求投影为 100% 且蓄能保持 0",
                core.TailUnknown0 == need1 && core.TailUnknown1 == 0,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 9999, 1);
            Check(
                "1星超出需求后仍封顶，不溢出",
                core.TailUnknown0 == need1,
                ref failures);

            // 3 星分母不同：同样的 1800 只值 37%，这是实机确认过的关键差异。
            EquipmentGrowthService.ApplyGrowthProjection(core, 1800, 3);
            Check(
                "3星成长值 1800 投影为 37% 进度",
                core.TailUnknown0 * 100 / need3 == 37,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 2400, 3);
            Check(
                "3星成长值 2400 投影为 49% 进度",
                core.TailUnknown0 * 100 / need3 == 49,
                ref failures);

            EquipmentGrowthService.ApplyGrowthProjection(core, 4800, 3);
            Check(
                "3星成长值 4800 投影为 99% 进度",
                core.TailUnknown0 * 100 / need3 == 99,
                ref failures);

            Check(
                "1星与3星成长需求不同（按星级取值）",
                need1 != need3,
                ref failures);
        }

        // 1/2 星经验满即自动升星；3 星起 PVF 第三列为 0，必须用卷轴，不能自动升。
        private static void VerifyAutoUpgradeStage(
            GameDatabase database,
            int growthItemId,
            ref int failures)
        {
            var core = ItemCore.Create(ItemCore.KindEquipment, growthItemId);
            var stage = 1;
            var need1 = EquipmentGrowthService.GetRequiredExp(1);

            var notFull = EquipmentGrowthService.TryAutoUpgradeStage(
                ref core,
                need1 - 1,
                stage,
                out _);
            Check(
                "未满档不触发自动升星",
                !notFull && core.ItemId == growthItemId,
                ref failures);

            var upgraded = EquipmentGrowthService.TryAutoUpgradeStage(
                ref core,
                need1,
                stage,
                out var newStage);
            Check(
                "1星满需求自动升为2星并换成下一星模板",
                upgraded
                    && newStage == 2
                    && core.ItemId == 101010188,
                ref failures);

            // 3 星在 PVF 里标注为需要材料，不允许自动升。
            var atCeiling = ItemCore.Create(ItemCore.KindEquipment, 101010189);
            var blocked = EquipmentGrowthService.TryAutoUpgradeStage(
                ref atCeiling,
                EquipmentGrowthService.GetRequiredExp(3),
                3,
                out _);
            Check(
                "3星满档不再自动升星（需卷轴）",
                !blocked && atCeiling.ItemId == 101010189,
                ref failures);
        }

        // 物品被删除(丢弃/出售/替换)时，它的成长值行必须一起消失，
        // 否则会留下永远指不到物品的孤儿行。
        private static void VerifyDeleteClearsGrowth(
            GameDatabase database,
            int growthItemId,
            ref int failures)
        {
            var inventory = LoadInventory(database, DeleteCharacterId, DeleteAccountId);
            var core = ItemCore.Create(ItemCore.KindEquipment, growthItemId);
            inventory.SetItem(InventoryListType.Main, MainWeaponSlot, core);
            var lease = CreateLease(inventory);
            try
            {
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    EquipmentGrowthService.AccumulateForEquippedWeapons(
                        inventory,
                        connection,
                        transaction,
                        5);
                    transaction.Commit();
                }

                var before = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
                Check(
                    "删除场景：成长值已落库",
                    before != null && before.GrowthValue == 5,
                    ref failures);
                if (before == null)
                    return;

                // 清空该槽 = 物品被删除，落库后成长值行应一并清除。
                inventory.SetItem(InventoryListType.Main, MainWeaponSlot, null);
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    InventoryPersistenceService.SaveDirtyInTransaction(
                        connection,
                        transaction,
                        lease);
                    transaction.Commit();
                }

                var after = ReadGrowth(database, inventory, InventoryListType.Main, MainWeaponSlot);
                Check(
                    "物品删除后成长值行一并清除",
                    after == null,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(lease.SessionId, inventory.CharacterId);
            }
        }

        private static InventoryLease CreateLease(InventoryService inventory)
        {
            return InventoryContext.Register(
                Guid.NewGuid(),
                inventory.CharacterId,
                inventory);
        }

        private static bool TryMove(
            InventoryService inventory,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            out InventoryMoveServiceResult result)
        {
            var request = new InventoryMoveRequest
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlotIndex,
                MoveCount = 1,
                DestinationListType = destinationListType,
                DestinationSlotIndex = destinationSlotIndex,
            };
            return InventoryMoveService.TryMove(inventory, request, ActorJob, 0, out result);
        }

        private static EquipmentGrowthRecord ReadGrowth(
            GameDatabase database,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var itemUid = InventoryItemRepository.LoadCharacterSlotUid(
                    connection,
                    null,
                    inventory.CharacterId,
                    listType,
                    slotIndex);
                if (itemUid <= 0)
                    return null;

                return EquipmentGrowthRepository.LoadGrowth(
                    connection,
                    null,
                    inventory.CharacterId,
                    itemUid);
            }
        }

        private static InventoryService LoadInventory(
            GameDatabase database,
            int characterId = TestCharacterId,
            int accountId = TestAccountId)
        {
            using (var connection = database.OpenConnection())
            {
                return InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
            }
        }

        private static void SeedAccountAndCharacter(GameDatabase database)
        {
            SeedAccountAndCharacter(database, TestAccountId, TestCharacterId, "equipment-growth");
            SeedAccountAndCharacter(database, MigrationAccountId, MigrationCharacterId, "equipment-growth-m");
            SeedAccountAndCharacter(database, DeleteAccountId, DeleteCharacterId, "equipment-growth-d");
            SeedAccountAndCharacter(database, SwapAccountId, SwapCharacterId, "equipment-growth-s");
        }

        private static void SeedAccountAndCharacter(
            GameDatabase database,
            int accountId,
            int characterId,
            string prefix)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", prefix + "-a");
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", prefix + "-c");
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
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
