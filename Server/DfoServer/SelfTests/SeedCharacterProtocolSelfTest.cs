using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class SeedCharacterProtocolSelfTest
    {
        private const int Marker16WireOffset = 22;

        public static int Run()
        {
            var failures = 0;

            Check(
                "equipment keeps the ITEM_LIST -1 marker sentinel",
                WriteCommonMarker(
                    ItemCore.KindEquipment,
                    ItemCore.Marker16Default) == ItemCore.Marker16Default,
                ref failures);
            Check(
                "stackable ITEM_LIST maps the internal -1 marker to wire zero",
                WriteCommonMarker(
                    ItemCore.KindMaterial,
                    ItemCore.Marker16Default) == 0,
                ref failures);
            Check(
                "explicit common ITEM_LIST markers are preserved",
                WriteCommonMarker(ItemCore.KindMaterial, 731) == 731,
                ref failures);
            Check(
                "avatar ITEM_LIST maps the internal -1 marker to wire zero",
                WriteAvatarMarker(ItemCore.Marker16Default) == 0,
                ref failures);
            Check(
                "non-creature pet ITEM_LIST maps the internal -1 marker to wire zero",
                WritePetMarker(ItemCore.Marker16Default) == 0,
                ref failures);
            Check(
                "creature ITEM_LIST keeps its resolved remaining-time marker",
                WriteCreatureMarker(541) == 541,
                ref failures);

            try
            {
                using (var connection = new SqliteConnection("Data Source=:memory:"))
                {
                    connection.Open();
                    CreateCreatureRows(connection);

                    var inventory = new InventoryService(1002, 1002);
                    inventory.CreatureDetails.LoadForCharacter(connection, 1002);
                    var snapshot = PetInventoryAccessor.BuildCreatureItemListSnapshot(inventory);
                    var actualOrder = snapshot.Entries
                        .Select(entry => entry.CreatureKey)
                        .ToArray();

                    Check(
                        "0x0069 creature details follow persisted sort_order",
                        actualOrder.SequenceEqual(new[] { 20, 10, 30 }),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] seed creature ordering threw: " + ex);
                failures++;
            }

            Console.WriteLine(
                failures == 0
                    ? "SeedCharacterProtocolSelfTest OK"
                    : "SeedCharacterProtocolSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static int WriteCommonMarker(byte itemKind, int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(itemKind, marker16);
            ItemListProtocolWriter.WriteCommonEntry84(writer, 3, core);
            return ReadMarker(writer);
        }

        private static int WriteAvatarMarker(int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(ItemCore.KindAvatar, marker16);
            ItemListProtocolWriter.WriteAvatarEntry126(writer, 0, core, null);
            return ReadMarker(writer);
        }

        private static int WritePetMarker(int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(ItemCore.KindCreatureEquipment, marker16);
            ItemListProtocolWriter.WritePetEntry84(writer, 0, core);
            return ReadMarker(writer);
        }

        private static int WriteCreatureMarker(int marker16)
        {
            var writer = new GamePacketWriter();
            var core = CreateCore(ItemCore.KindCreature, marker16);
            ItemListProtocolWriter.WritePetCreatureEntry84(writer, 0, core, null);
            return ReadMarker(writer);
        }

        private static ItemCore CreateCore(byte itemKind, int marker16)
        {
            var core = ItemCore.Create(itemKind, 0);
            core.Marker16 = marker16;
            return core;
        }

        private static int ReadMarker(GamePacketWriter writer)
        {
            var body = writer.ToArray();
            return BitConverter.ToInt32(body, Marker16WireOffset);
        }

        private static void CreateCreatureRows(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE character_creatures (
    character_id INTEGER NOT NULL,
    sort_order INTEGER NOT NULL,
    creature_key INTEGER NOT NULL,
    field04 INTEGER NOT NULL,
    mode_flag INTEGER NOT NULL,
    progress_value INTEGER NOT NULL,
    mode1_field0a INTEGER NOT NULL,
    mode1_field0b INTEGER NOT NULL,
    field_after_value INTEGER NOT NULL,
    creature_text BLOB NOT NULL,
    tail_flag INTEGER NOT NULL,
    extra_json TEXT NOT NULL
);
CREATE INDEX idx_character_creatures_key
    ON character_creatures(character_id, creature_key);
INSERT INTO character_creatures VALUES
    (1002, 2, 30, 100, 0, 30, 0, 0, 3, X'33', 0, '{}'),
    (1002, 1, 10, 100, 0, 10, 0, 0, 2, X'31', 0, '{}'),
    (1002, 0, 20, 100, 0, 20, 0, 0, 1, X'32', 0, '{}');";
                command.ExecuteNonQuery();
            }
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }
    }
}
