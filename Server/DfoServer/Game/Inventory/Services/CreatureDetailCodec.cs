using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class CreatureDetailCodec
    {
        internal static CreatureDetail ReadDetail(SqliteDataReader reader)
        {
            return FromRecord(ReadRecord(reader));
        }

        internal static CreatureDetailRecord ReadRecord(SqliteDataReader reader)
        {
            return new CreatureDetailRecord
            {
                CharacterId = reader.GetInt32(0),
                SortOrder = reader.GetInt32(1),
                CreatureKey = reader.GetInt32(2),
                Field04 = Convert.ToByte(reader.GetInt32(3), CultureInfo.InvariantCulture),
                ModeFlag = Convert.ToByte(reader.GetInt32(4), CultureInfo.InvariantCulture),
                ProgressValue32 = reader.GetInt32(5),
                Mode1Field0A = Convert.ToByte(reader.GetInt32(6), CultureInfo.InvariantCulture),
                Mode1Field0B = Convert.ToByte(reader.GetInt32(7), CultureInfo.InvariantCulture),
                FieldAfterValue32 = reader.GetInt32(8),
                CreatureText = reader.IsDBNull(9) ? Array.Empty<byte>() : (byte[])reader[9],
                TailFlag = Convert.ToByte(reader.GetInt32(10), CultureInfo.InvariantCulture),
                ExtraJson = reader.IsDBNull(11) ? "{}" : reader.GetString(11),
            };
        }

        internal static CreatureDetail FromRecord(CreatureDetailRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            return new CreatureDetail
            {
                Uid = record.CreatureKey,
                NameBytes = Copy(record.CreatureText),
                Field04 = record.Field04,
                ModeFlag = record.ModeFlag,
                Mode1Field0A = record.Mode1Field0A,
                Mode1Field0B = record.Mode1Field0B,
                ProgressValue32 = record.ProgressValue32,
                FieldAfterValue32 = record.FieldAfterValue32,
                TailFlag = record.TailFlag,
            };
        }

        internal static CreatureDetailRecord ToRecord(CreatureDetail detail, CreatureDetailRecord source = null)
        {
            if (detail == null)
                throw new ArgumentNullException(nameof(detail));

            return new CreatureDetailRecord
            {
                CharacterId = source?.CharacterId ?? 0,
                SortOrder = source?.SortOrder ?? 0,
                CreatureKey = detail.Uid,
                Field04 = detail.Field04,
                ModeFlag = detail.ModeFlag,
                ProgressValue32 = detail.ProgressValue32,
                Mode1Field0A = detail.Mode1Field0A,
                Mode1Field0B = detail.Mode1Field0B,
                FieldAfterValue32 = detail.FieldAfterValue32,
                CreatureText = detail.NameBytes,
                TailFlag = detail.TailFlag,
                ExtraJson = string.IsNullOrWhiteSpace(source?.ExtraJson) ? "{}" : source.ExtraJson,
            };
        }

        private static byte[] Copy(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            var result = new byte[data.Length];
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
            return result;
        }

        internal sealed class CreatureDetailRecord
        {
            public int CharacterId { get; set; }

            public int SortOrder { get; set; }

            public int CreatureKey { get; set; }

            public byte Field04 { get; set; }

            public byte ModeFlag { get; set; }

            public int ProgressValue32 { get; set; }

            public byte Mode1Field0A { get; set; }

            public byte Mode1Field0B { get; set; }

            public int FieldAfterValue32 { get; set; }

            public byte[] CreatureText { get; set; } = Array.Empty<byte>();

            public byte TailFlag { get; set; }

            public string ExtraJson { get; set; } = "{}";
        }
    }
}
