using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InvenItemCodec
    {
        internal static InventoryItem ReadItem(SqliteDataReader reader)
        {
            return FromRecord(ReadRecord(reader));
        }

        internal static InvenItemRecord ReadRecord(SqliteDataReader reader)
        {
            return new InvenItemRecord
            {
                ItemUid = reader.GetInt64(0),
                OwnerScope = reader.GetString(1),
                OwnerId = reader.GetInt32(2),
                CharacterId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                ListType = (InventoryListType)reader.GetInt32(4),
                SlotIndex = Convert.ToInt16(reader.GetInt32(5), CultureInfo.InvariantCulture),
                ItemCoreBlob = reader.IsDBNull(6) ? Array.Empty<byte>() : (byte[])reader[6],
                CreatedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
            };
        }

        internal static InventoryItem FromRecord(InvenItemRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            return new InventoryItem
            {
                ItemUid = record.ItemUid,
                OwnerScope = string.IsNullOrWhiteSpace(record.OwnerScope) ? "character" : record.OwnerScope,
                OwnerId = record.OwnerId,
                CharacterId = record.CharacterId,
                ListType = record.ListType,
                SlotIndex = record.SlotIndex,
                Core = ItemCore.FromBytes(record.ItemCoreBlob),
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
            };
        }

        internal static InvenItemRecord ToRecord(InventoryItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            return new InvenItemRecord
            {
                ItemUid = item.ItemUid,
                OwnerScope = string.IsNullOrWhiteSpace(item.OwnerScope) ? "character" : item.OwnerScope,
                OwnerId = item.OwnerId,
                CharacterId = item.CharacterId,
                ListType = item.ListType,
                SlotIndex = item.SlotIndex,
                ItemCoreBlob = (item.Core ?? new ItemCore()).ToBytes(),
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
            };
        }

        internal sealed class InvenItemRecord
        {
            public long ItemUid { get; set; }

            public string OwnerScope { get; set; } = "character";

            public int OwnerId { get; set; }

            public int? CharacterId { get; set; }

            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public byte[] ItemCoreBlob { get; set; } = Array.Empty<byte>();

            public string CreatedAt { get; set; }

            public string UpdatedAt { get; set; }
        }
    }
}
