using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class EquipmentItemLockRepository
    {
        internal static Dictionary<byte, EquipmentItemLock> LoadForCharacter(SqliteConnection connection, int characterId)
        {
            var result = new Dictionary<byte, EquipmentItemLock>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT equipment_lock_id, state, remaining_seconds
FROM character_item_locks
WHERE character_id = @cid
  AND equipment_lock_id > 0
ORDER BY equipment_lock_id;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var equipmentLockId = Convert.ToByte(reader.GetInt32(0), CultureInfo.InvariantCulture);
                        result[equipmentLockId] = new EquipmentItemLock
                        {
                            EquipmentLockId = equipmentLockId,
                            State = Convert.ToByte(reader.GetInt32(1), CultureInfo.InvariantCulture),
                            RemainingSeconds = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        };
                    }
                }
            }

            return result;
        }
    }
}
