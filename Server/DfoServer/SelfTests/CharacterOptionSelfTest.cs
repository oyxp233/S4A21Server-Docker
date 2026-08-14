using System;
using System.IO;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Settings;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace DfoServer.SelfTests
{
    public static class CharacterOptionSelfTest
    {
        private const int AccountId = 8101;
        private const int CharacterId = 8101001;

        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_OPTION selftest ===");
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok)
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine($"  [PASS] {name}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  [FAIL] {name}");
                }
            }

            var tempDb = Path.Combine(Path.GetTempPath(), "character_option_selftest.db");
            DeleteTempDatabase(tempDb);

            try
            {
                SeedCharacter(tempDb);

                var optionBody = BuildCapturedShapeOptionBody(toggleOffset: 8, enabled: true);
                var stateRepo = new SqliteCharacterStateRepository(tempDb, ServerPaths.SchemaFilePath);
                stateRepo.SaveCharacterOption(CharacterId, optionBody);

                var loaded = new SelectCharacterInitializationSnapshot();
                stateRepo.LoadAll(CharacterId, loaded);
                Check("saved character option blob loads", ByteEquals(loaded.CharacterOptionBlob, optionBody));

                stateRepo.SaveFlags(CharacterId, new SelectCharacterInitializationSnapshot
                {
                    AckTutorialSkipable = 1,
                });
                var afterSaveFlags = new SelectCharacterInitializationSnapshot();
                stateRepo.LoadAll(CharacterId, afterSaveFlags);
                Check("SaveFlags preserves existing character option", ByteEquals(afterSaveFlags.CharacterOptionBlob, optionBody));

                var charRepo = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
                var dataSource = new SqliteSelectCharacterDataSource(tempDb, ServerPaths.SchemaFilePath, charRepo);
                var snapshot = dataSource.Load(CharacterId, AccountId);
                Check("select data source preserves character option", ByteEquals(snapshot.InitializationSnapshot.CharacterOptionBlob, optionBody));

                var builder = new CharacterOptionBodyBuilder();
                var built = builder.TryBuild(snapshot, 0, out var builtBody);
                Check("0x0187 builder succeeds", built);
                Check("0x0187 builder returns exact saved body", ByteEquals(builtBody, optionBody));

                Check("fresh character mood popup defaults to normal",
                    snapshot.CharacterRecord.Subtype0Tail != null
                    && snapshot.CharacterRecord.Subtype0Tail.MoodValue == 0
                    && snapshot.CharacterRecord.Subtype0Tail.EmotionIndex == 0
                    && snapshot.CharacterRecord.Subtype0Tail.ActionByte == 0);

                snapshot.CharacterRecord.Subtype0Tail.EmotionIndex = 0x1234;
                snapshot.CharacterRecord.Subtype0Tail.ActionByte = 0x56;
                using (var conn = new SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
                {
                    conn.Open();
                    SqliteSubtype0FieldsRepository.Save(
                        conn, CharacterId, snapshot.CharacterRecord.Subtype0Tail);
                }
                stateRepo.SaveMoodValue(CharacterId, 6);
                var snapshotWithMood = dataSource.Load(CharacterId, AccountId);
                Check("change mood persists to subtype0 tail",
                    snapshotWithMood.CharacterRecord.Subtype0Tail != null
                    && snapshotWithMood.CharacterRecord.Subtype0Tail.MoodValue == 6);
                Check("change mood leaves emotion visual field untouched",
                    snapshotWithMood.CharacterRecord.Subtype0Tail != null
                    && snapshotWithMood.CharacterRecord.Subtype0Tail.EmotionIndex == 0x1234);
                Check("change mood leaves action visual field untouched",
                    snapshotWithMood.CharacterRecord.Subtype0Tail != null
                    && snapshotWithMood.CharacterRecord.Subtype0Tail.ActionByte == 0x56);
                Check("change mood leaves legacy channel_id untouched",
                    ReadLegacyChannelId(tempDb) == 2);

                var mainOption = CopyBytes(AccountSettings.DefaultMainGameOption);
                mainOption[36] = 0x05;
                var accountSettingsRepo = new AccountSettingsRepository(tempDb, ServerPaths.SchemaFilePath);
                accountSettingsRepo.SaveMainOption(AccountId, mainOption);
                var snapshotWithMainOption = dataSource.Load(CharacterId, AccountId);
                Check("main game option uses account settings", ByteEquals(snapshotWithMainOption.InitializationSnapshot.MainGameOptionBlob, mainOption));

                WriteOption(mainOption, AccountSettings.FullAvatarOptionIndex, false);
                WriteOption(mainOption, AccountSettings.VisibleGrowAvatarOptionIndex, false);
                Check("hidden avatar options map visible bits 3 to 9",
                    AccountSettings.TryApplyCharacterVisibilityOptions(mainOption, 3, out var hiddenBits)
                    && hiddenBits == 9);
                Check("avatar option mapping preserves unrelated bits",
                    AccountSettings.TryApplyCharacterVisibilityOptions(mainOption, 0x73, out var preservedBits)
                    && preservedBits == 0x79);

                WriteOption(mainOption, AccountSettings.FullAvatarOptionIndex, true);
                WriteOption(mainOption, AccountSettings.VisibleGrowAvatarOptionIndex, true);
                Check("visible avatar options map visible bits 9 to 3",
                    AccountSettings.TryApplyCharacterVisibilityOptions(mainOption, 9, out var visibleBits)
                    && visibleBits == 3);

                WriteOption(mainOption, AccountSettings.FullAvatarOptionIndex, false);
                WriteOption(mainOption, AccountSettings.VisibleGrowAvatarOptionIndex, false);
                var visibilityPersistence = new CharacterVisibilitySettingsPersistence(tempDb, ServerPaths.SchemaFilePath);
                visibilityPersistence.Save(AccountId, CharacterId, mainOption, hiddenBits);
                var subtypeRepo = new SqliteSubtype0FieldsRepository(tempDb, ServerPaths.SchemaFilePath);
                Check("visibility settings persist account option and character bits atomically",
                    ByteEquals(accountSettingsRepo.Load(AccountId)?.MainGameOption, mainOption)
                    && subtypeRepo.Load(CharacterId)?.UserStateBits == 9);

                var rejectedOption = CopyBytes(mainOption);
                WriteOption(rejectedOption, AccountSettings.VisibleGrowAvatarOptionIndex, false);
                var rolledBack = false;
                try
                {
                    visibilityPersistence.Save(AccountId, CharacterId + 1, rejectedOption, 0);
                }
                catch (SqliteException)
                {
                    rolledBack = true;
                }
                Check("visibility settings roll back account option when character save fails",
                    rolledBack
                    && ByteEquals(accountSettingsRepo.Load(AccountId)?.MainGameOption, mainOption));

                Check("character visibility refresh body is uid plus visible bits",
                    ByteEquals(
                        CharacterVisibilityBodyBuilder.Build(0x1234, 9),
                        new byte[] { 0x34, 0x12, 0x09 }));

                var characterHotkeys = BuildHotkeyBlob(0x004D, 0x1234, 0x5678, 0x0099);
                stateRepo.SaveHotkeyConfig(CharacterId, characterHotkeys);
                accountSettingsRepo.SaveHotkeySlots(AccountId, BuildHotkeyBlob(0x0002));
                var snapshotWithHotkeys = dataSource.Load(CharacterId, AccountId);
                Check("hotkeys load from character_hotkey_slots",
                    snapshotWithHotkeys.InitializationSnapshot.HotkeyConfigSlots.Count >= 4
                    && snapshotWithHotkeys.InitializationSnapshot.HotkeyConfigSlots[0] == 0x004D
                    && snapshotWithHotkeys.InitializationSnapshot.HotkeyConfigSlots[1] == 0x1234
                    && snapshotWithHotkeys.InitializationSnapshot.HotkeyConfigSlots[2] == 0x5678);

                var loginPackets = AccountSettingsPacketBuilder.BuildLoginAccountSettings(null);
                Check("login sends account-scoped hotkey prefix for rapid-fire",
                    loginPackets.Count == 2
                    && BitConverter.ToUInt16(loginPackets[1], 1) == 0x01C7
                    && ReadLoginHotkeyPayloadLength(loginPackets[1]) == AccountSettings.AccountScopedHotkeySlotCount * 2);
                Check("login hotkey prefix keeps rapid-fire enabled",
                    ReadLoginHotkeyPrefix(loginPackets[1]) == 0x0002);
                Check("login hotkey prefix does not overwrite character movement-up key",
                    snapshotWithHotkeys.InitializationSnapshot.HotkeyConfigSlots.Count > 0
                    && snapshotWithHotkeys.InitializationSnapshot.HotkeyConfigSlots[0] == 0x004D);

                var creatorDefaults = CharacterKeyboardDefaults.BuildHotkeySlots(10);
                Check("creator default hotkey body has creator slot count",
                    creatorDefaults.Length == 168);
                Check("creator default hotkeys differ from normal defaults",
                    !CharacterKeyboardDefaults.LooksLikeNormalDefaultHotkeySlots(creatorDefaults));
                Check("creator default hotkeys include WASD and Space virtual keys",
                    ContainsHotkeyValue(creatorDefaults, 0x0057)
                    && ContainsHotkeyValue(creatorDefaults, 0x0041)
                    && ContainsHotkeyValue(creatorDefaults, 0x0053)
                    && ContainsHotkeyValue(creatorDefaults, 0x0044)
                    && ContainsHotkeyValue(creatorDefaults, 0x0020));

                Check("creator grow type 0 is creator name in PVF",
                    CreatorGrowTypeNameAt(0) == "缔造者");
                Check("creator grow type 1 is not creator name in PVF",
                    CreatorGrowTypeNameAt(1) == "元素师");
            }
            finally
            {
                DeleteTempDatabase(tempDb);
            }

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }

        private static byte[] BuildCapturedShapeOptionBody(int toggleOffset, bool enabled)
        {
            var body = new byte[516];
            for (int i = 0; i < body.Length; i += 4)
            {
                body[i] = 0xC5;
                if (i + 1 < body.Length) body[i + 1] = 0x82;
                if (i + 2 < body.Length) body[i + 2] = 0xEC;
                if (i + 3 < body.Length) body[i + 3] = 0xC5;
            }

            body[0] = 0xC5;
            body[1] = 0x80;
            body[2] = 0xEC;
            body[3] = 0xC5;

            if (toggleOffset >= 0 && toggleOffset + 3 < body.Length)
            {
                if (enabled)
                {
                    body[toggleOffset] = 0x91;
                    body[toggleOffset + 1] = 0x82;
                    body[toggleOffset + 2] = 0x13;
                    body[toggleOffset + 3] = 0x3A;
                }
                else
                {
                    body[toggleOffset] = 0x3A;
                    body[toggleOffset + 1] = 0x7D;
                    body[toggleOffset + 2] = 0x13;
                    body[toggleOffset + 3] = 0x3A;
                }
            }

            return body;
        }

        private static byte[] BuildHotkeyBlob(params ushort[] values)
        {
            var body = new byte[values.Length * 2];
            for (var i = 0; i < values.Length; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, body, i * 2, 2);
            return body;
        }

        private static bool ContainsHotkeyValue(byte[] hotkeys, ushort value)
        {
            for (var i = 0; i + 1 < hotkeys.Length; i += 2)
            {
                if (BitConverter.ToUInt16(hotkeys, i) == value)
                    return true;
            }
            return false;
        }

        private static int ReadLoginHotkeyPayloadLength(byte[] packet)
        {
            return packet != null && packet.Length >= 20
                ? BitConverter.ToInt32(packet, 16)
                : -1;
        }

        private static ushort ReadLoginHotkeyPrefix(byte[] packet)
        {
            return packet != null && packet.Length >= 22
                ? BitConverter.ToUInt16(packet, 20)
                : (ushort)0;
        }

        private static string CreatorGrowTypeNameAt(int growType)
        {
            var text = PvfArchiveAccessor.ReadText("character/Mage/CreatorMage.chr");
            var match = Regex.Match(
                text,
                @"\[growtype name\]\s*((?:`[^`]+`\s*)+)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            var names = Regex.Matches(match.Groups[1].Value, @"`([^`]+)`");
            return growType >= 0 && growType < names.Count
                ? names[growType].Groups[1].Value
                : null;
        }

        private static byte[] CopyBytes(byte[] source)
        {
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        private static void SeedCharacter(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'character-option-selftest', '');

INSERT OR IGNORE INTO characters
    (character_id, account_id, name, job, grow_type, level, town_id, area_id, pos_x, pos_y)
VALUES
    (@cid, @aid, 'CharOptSelfTest', 0, 0, 1, 1, 1, 100, 100);

INSERT OR IGNORE INTO character_init_flags (character_id)
VALUES (@cid);";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static int ReadLegacyChannelId(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT channel_id FROM character_subtype0_fields WHERE character_id = @cid";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        private static void WriteOption(byte[] mainGameOption, int optionIndex, bool enabled)
        {
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)(enabled ? 1 : 0)), 0, mainGameOption, optionIndex * 2, 2);
        }

        private static bool ByteEquals(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private static void DeleteTempDatabase(string path)
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
