using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    // 单件龙跃武器实例的成长值状态。
    internal sealed class EquipmentGrowthRecord
    {
        public long ItemUid { get; set; }

        public int ItemId { get; set; }

        public int GrowthValue { get; set; }

        public int Stage { get; set; } = 1;
    }

    // 龙跃武器成长值的持久化 owner。
    // 成长值按 (character_id, item_uid) 关联到 character_inventory_items 的行：
    // 跨列表移动(背包<->穿戴)时 item_uid 会变，迁移在 InventoryPersistenceService
    // 的同一事务内完成，保证成长值始终跟随同一件武器实例。
    internal static class EquipmentGrowthRepository
    {
        internal static Dictionary<long, EquipmentGrowthRecord> LoadForCharacter(
            SqliteConnection connection,
            int characterId)
        {
            var result = new Dictionary<long, EquipmentGrowthRecord>();
            if (connection == null || characterId <= 0)
                return result;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid, item_id, growth_value, stage
FROM character_equipment_growth
WHERE character_id = @cid
ORDER BY item_uid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var itemUid = reader.GetInt64(0);
                        result[itemUid] = new EquipmentGrowthRecord
                        {
                            ItemUid = itemUid,
                            ItemId = reader.GetInt32(1),
                            GrowthValue = reader.GetInt32(2),
                            Stage = Math.Max(1, reader.GetInt32(3)),
                        };
                    }
                }
            }

            return result;
        }

        // 读取单个实例的成长值状态。行不存在时返回 null（该武器还没有成长值）。
        internal static EquipmentGrowthRecord LoadGrowth(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long itemUid)
        {
            if (connection == null
                || characterId <= 0
                || itemUid <= 0)
            {
                return null;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, item_id, growth_value, stage
FROM character_equipment_growth
WHERE character_id = @cid
  AND item_uid = @uid
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@uid", itemUid);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new EquipmentGrowthRecord
                    {
                        ItemUid = reader.GetInt64(0),
                        ItemId = reader.GetInt32(1),
                        GrowthValue = reader.GetInt32(2),
                        Stage = Math.Max(1, reader.GetInt32(3)),
                    };
                }
            }
        }

        // 累加成长值。返回 false 表示该实例没有成长值行且无法建立（调用方应视为失败）。
        internal static bool AddGrowth(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long itemUid,
            int itemId,
            int delta,
            int stage)
        {
            if (connection == null
                || transaction == null
                || characterId <= 0
                || itemUid <= 0
                || itemId <= 0
                || delta <= 0)
            {
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_equipment_growth (
    character_id, item_uid, item_id, growth_value, stage, updated_at
) VALUES (
    @cid, @uid, @itemId, @delta, @stage, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id, item_uid) DO UPDATE SET
    growth_value = character_equipment_growth.growth_value + excluded.growth_value,
    item_id = excluded.item_id,
    stage = excluded.stage,
    updated_at = CURRENT_TIMESTAMP
WHERE character_equipment_growth.growth_value + excluded.growth_value <= @maxValue;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@uid", itemUid);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@delta", delta);
                command.Parameters.AddWithValue("@stage", Math.Max(1, stage));
                command.Parameters.AddWithValue("@maxValue", MaxGrowthValue);
                return command.ExecuteNonQuery() > 0;
            }
        }

        // 跨列表移动后把成长值迁到新的 item_uid。旧行不存在时视为无事可做。
        internal static bool MoveToNewItemUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long oldItemUid,
            long newItemUid)
        {
            if (connection == null
                || transaction == null
                || characterId <= 0
                || oldItemUid <= 0
                || newItemUid <= 0
                || oldItemUid == newItemUid)
            {
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE OR REPLACE character_equipment_growth
SET item_uid = @newUid,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND item_uid = @oldUid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@oldUid", oldItemUid);
                command.Parameters.AddWithValue("@newUid", newItemUid);
                return command.ExecuteNonQuery() > 0;
            }
        }

        // 物品被删除(丢弃/出售/分解/邮件领取等)时清掉其成长值。
        internal static void DeleteForItemUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long itemUid)
        {
            if (connection == null
                || transaction == null
                || characterId <= 0
                || itemUid <= 0)
            {
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_equipment_growth
WHERE character_id = @cid
  AND item_uid = @uid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@uid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        // 跨列表交换场景用：把一份成长值快照整体写到目标 item_uid。
        // 迁移必须先把旧行读进内存再写回，避免双向交换时 UPDATE 互相顶掉。
        internal static void WriteGrowthSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long itemUid,
            int itemId,
            int growthValue,
            int stage)
        {
            if (connection == null
                || transaction == null
                || characterId <= 0
                || itemUid <= 0)
            {
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_equipment_growth (
    character_id, item_uid, item_id, growth_value, stage, updated_at
) VALUES (
    @cid, @uid, @itemId, @growthValue, @stage, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@uid", itemUid);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@growthValue", Math.Max(0, growthValue));
                command.Parameters.AddWithValue("@stage", Math.Max(1, stage));
                command.ExecuteNonQuery();
            }
        }

        internal const int MaxGrowthValue = 1000000;
    }
}
