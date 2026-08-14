using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mercenary;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class StrikerSkillSelfTest
    {
        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== Striker skill self-test ===");

            var all = StrikerSkillDataProvider.GetAll();
            Check("PVF striker skill entries loaded", all.Count > 0);

            var mage = StrikerSkillDataProvider.GetAvailableSkills(job: 3, growType: 1, level: 86);
            Check("mage growType=1 Lv86 has striker skills", mage.Count > 0);

            var fighter = StrikerSkillDataProvider.GetAvailableSkills(job: 1, growType: 1, level: 86);
            Check("fighter growType=1 Lv86 has striker skills", fighter.Count > 0);

            var invalidGrow = StrikerSkillDataProvider.GetAvailableSkills(job: 3, growType: 0, level: 86);
            Check("mage growType=0 has no striker skills in PVF", invalidGrow.Count == 0);

            var packedSwordman = StrikerSkillDataProvider.GetAvailableSkills(job: 0, growType: 35, level: 86);
            Check("packed swordman growType=35 maps to PVF growType=3", packedSwordman.Count > 0);
            Check("striker minimum support level comes from linksystem PVF",
                StrikerSkillDataProvider.GetMinimumSupportLevel() == 70);
            Check("striker active support count comes from striker PVF",
                StrikerSkillDataProvider.GetMaxActiveSupportCount() == 1);
            var zeroCombo = all.First(entry => entry.ComboIndex == 0);
            Check("striker combo zero remains an exact legal state value",
                StrikerSkillDataProvider.FindBySkill(
                    zeroCombo.Job, zeroCombo.GrowType, zeroCombo.SkillIndex, zeroCombo.ComboIndex) != null);
            var nonZeroCombo = all.First(entry => entry.ComboIndex > 0);
            Check("striker state rejects combo-zero wildcard for nonzero combo skill",
                StrikerSkillDataProvider.FindBySkill(
                    nonZeroCombo.Job, nonZeroCombo.GrowType, nonZeroCombo.SkillIndex, 0) == null);
            Check("0x01E8 success ACK contains only consumed result byte",
                MercenaryHandler.BuildSelectSuccessAck().SequenceEqual(new byte[] { 0x01 }));
            Check("0x01E5 failure ACK contains result and mandatory error byte",
                MercenaryHandler.BuildSkillListFailureAck().SequenceEqual(new byte[] { 0x00, 0x00 }));
            Check("0x01E8 failure ACK contains result and mandatory error byte",
                MercenaryHandler.BuildSelectFailureAck().SequenceEqual(new byte[] { 0x00, 0x00 }));
            Check("PVF-legal unlearned support skill remains selectable without a learned-level fallback",
                StrikerSkillDataProvider.GetAvailableSkills(job: 0, growType: 4, level: 85)
                    .Any(entry => entry.SkillIndex == 74 && entry.ComboIndex == 43));
            var normalizedUnlearnedPage = StrikerSupportTagCharacterPacketBuilder.NormalizeSkillPageEntriesForTest(
                new MercenarySupportState { SkillId = 74, StrikerSkillId = 43 },
                new[]
                {
                    new SkillInfoEntrySnapshot { Slot = 54, SkillId = 33, Level = 10 },
                    new SkillInfoEntrySnapshot { Slot = 198, SkillId = 72, Level = 23 },
                });
            Check("0x019F unlearned selected skill keeps real page without synthetic slot/level",
                normalizedUnlearnedPage.Count == 2
                && !normalizedUnlearnedPage.Any(entry => entry.SkillId == 74)
                && normalizedUnlearnedPage.Select(entry => entry.Slot).SequenceEqual(new byte[] { 54, 198 }));
            var allDefaultAvatarRows = StrikerDefaultAvatarDataProvider.GetAllForTest().Values
                .Where(defaults => defaults != null)
                .ToList();
            Check("all PVF default-avatar positive slots 0..10 match their equipment types",
                allDefaultAvatarRows.All(defaults =>
                    defaults.Count >= 11
                    && Enumerable.Range(0, 11).All(slot =>
                        defaults[slot] <= 0
                        || StrikerSupportTagCharacterPacketBuilder.IsEquipmentSlotMatchForTest(
                            (byte)slot,
                            defaults[slot]))));
            Check("PVF default-avatar slots 9/10 remain nonpositive",
                allDefaultAvatarRows.All(defaults => defaults.Count >= 11
                    && defaults[9] <= 0
                    && defaults[10] <= 0));
            Check("equipment slot validator accepts a real weapon in weapon slot",
                StrikerSupportTagCharacterPacketBuilder.IsEquipmentSlotMatchForTest(11, 101040019));
            Check("equipment slot validator rejects the same weapon in an avatar slot",
                !StrikerSupportTagCharacterPacketBuilder.IsEquipmentSlotMatchForTest(0, 101040019));

            CheckMercenarySupportRepository();
            CheckMercenaryWireSlotMapping();
            CheckSelectCharacterTagLifecycle();
            CheckAdventureGroupLevelCalculation();
            CheckTagRecordSerializerBoundary();

            Console.WriteLine("sample: " + string.Join(", ", mage.Take(3).Select(x => $"{x.SkillIndex}/{x.ComboIndex}")));
            return _failures == 0 ? 0 : 1;
        }

        private static int _failures;

        private static void CheckSelectCharacterTagLifecycle()
        {
            var duplicateTemplates = new List<SelectCharacterPacketTemplate>
            {
                new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.Raw,
                    Command = 0x00,
                    Type = 0x019F,
                    OccurrenceIndex = 7,
                },
                new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.Raw,
                    Command = 0x00,
                    Type = 0x019F,
                    OccurrenceIndex = 9,
                },
            };
            var duplicatePackets = SelectCharacterPacketBuilder.BuildPacketStream(
                new FixedSelectCharacterDataSource(new SelectCharacterDataSnapshot()),
                0,
                0,
                duplicateTemplates).ToList();
            Check("select-character emits exactly one dynamic 0x019F when templates duplicate it",
                CountPackets(duplicatePackets, 0x00, 0x019F) == 1);

            var missingTemplates = new List<SelectCharacterPacketTemplate>
            {
                new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.Raw,
                    Command = 0x00,
                    Type = 0x0331,
                },
            };
            var missingPackets = SelectCharacterPacketBuilder.BuildPacketStream(
                new FixedSelectCharacterDataSource(new SelectCharacterDataSnapshot()),
                0,
                0,
                missingTemplates).ToList();
            Check("select-character injects one empty dynamic 0x019F when templates omit it",
                CountPackets(missingPackets, 0x00, 0x019F) == 1
                && missingPackets.Any(packet =>
                    packet.Length == 17
                    && packet[0] == 0x00
                    && BitConverter.ToUInt16(packet, 1) == 0x019F
                    && packet[15] == 0x00
                    && packet[16] == 0x00));
        }

        private static int CountPackets(IEnumerable<byte[]> packets, byte command, ushort type)
        {
            return packets.Count(packet =>
                packet != null
                && packet.Length >= 15
                && packet[0] == command
                && BitConverter.ToUInt16(packet, 1) == type);
        }

        private static void CheckMercenaryWireSlotMapping()
        {
            var roster = new List<DfoServer.Game.Characters.CharacterRecord>
            {
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1002, Level = 86 },
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1003, Level = 86 },
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1004, Level = 86 },
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 1005, Level = 86 },
            };

            var slot2 = MercenaryHandler.FindCandidateByWireIndexForTest(roster, activeCharacterId: 1003, wireIndex: 2);
            var active = MercenaryHandler.FindCandidateByWireIndexForTest(roster, activeCharacterId: 1003, wireIndex: 1);

            Check("mercenary wire slot uses account roster index", slot2 != null && slot2.CharacterId == 1004);
            Check("mercenary wire slot rejects active character", active == null);
        }

        private static void CheckAdventureGroupLevelCalculation()
        {
            var roster = new List<DfoServer.Game.Characters.CharacterRecord>();
            for (var i = 0; i < 8; i++)
                roster.Add(new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92000 + i, Level = 86 });
            roster.Add(new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92010, Level = 1 });
            roster.Add(new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92011, Level = 1 });

            const int expectedLevel86Point = 10785; // 40 到 86 级逐级累加。
            const int expectedTotalPoint = expectedLevel86Point * 8; // 8 个 86 级角色贡献。
            const byte expectedManageLevel = 7;
            const ushort expectedManageOption = 125;

            var level86Summary = AdventureGroupDataProvider.Calculate(new[]
            {
                new DfoServer.Game.Characters.CharacterRecord { CharacterId = 92100, Level = 86 }
            });

            var summary = AdventureGroupDataProvider.Calculate(roster);
            Check("adventure group point accumulates level40-86 bonus for one Lv86 character", level86Summary.TotalPoint == expectedLevel86Point);
            Check("adventure group point uses cumulative PVF character level table", summary.TotalPoint == expectedTotalPoint);
            Check("adventure group level uses PVF account point thresholds", summary.ManageLevel == expectedManageLevel);
            Check("adventure group exp bonus uses account manage level", summary.ExpBonusPercent == 10);
            Check("adventure group manage option uses account manage level", summary.ManageOption == expectedManageOption);

            var body = AccountCharacterListBodyBuilder.Build(roster, new GetUserInfoTemplate
            {
                GateOrCount1 = 32,
                GateOrCount2 = 32,
            }, out var bodySummary);
            Check("USERINFO subtype2 writes adventure group level", body.Length > 10 && body[5] == bodySummary.ManageLevel && body[5] == expectedManageLevel);
            Check("USERINFO subtype2 writes adventure group point", body.Length > 10 && BitConverter.ToInt32(body, 6) == expectedTotalPoint);

            var addition = new DfoServer.Game.SelectCharacter.UserInfoAdditionSnapshot
            {
                StatPhysicalAttack = 10,
                StatPhysicalDefense = 20,
                StatMagicalAttack = 30,
                StatMagicalDefense = 40,
            };
            AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, roster);
            Check("USERINFO subtype1 applies account adventure group level", addition.ManageLevel == expectedManageLevel);
            Check("USERINFO subtype1 writes adventure group option index byte", addition.FlagByte == expectedManageLevel);
            Check("adventure group option does not modify base primary stats",
                addition.StatPhysicalAttack == 10 &&
                addition.StatPhysicalDefense == 20 &&
                addition.StatMagicalAttack == 30 &&
                addition.StatMagicalDefense == 40);
        }

        private static void CheckMercenarySupportRepository()
        {
            var tempDb = Path.Combine(
                Path.GetDirectoryName(ServerPaths.DatabasePath) ?? AppContext.BaseDirectory,
                "dfo-striker-support-selftest-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (1, 'striker-support-selftest', '');

INSERT INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (91001, 1, 'owner', 3, 0, 1),
       (91002, 1, 'support', 0, 35, 86);";
                        cmd.ExecuteNonQuery();
                    }
                }

                var repo = new SqliteMercenarySupportRepository(tempDb, ServerPaths.SchemaFilePath);
                repo.Save(new MercenarySupportState
                {
                    OwnerCharacterId = 91001,
                    Slot = 0,
                    SupportCharacterId = 91002,
                    SkillId = 81,
                    StrikerSkillId = 3,
                });

                var loaded = repo.LoadSlot(91001, 0);
                Check("mercenary support state saved", loaded != null && loaded.SupportCharacterId == 91002 && loaded.SkillId == 81 && loaded.StrikerSkillId == 3);
                Check("mercenary support state enables subtype0 link", ReadSubtype0Link(tempDb, 91001) == "1/4/1");

                repo.Save(new MercenarySupportState
                {
                    OwnerCharacterId = 91001,
                    Slot = 0,
                    SupportCharacterId = 91002,
                    SkillId = 24,
                    StrikerSkillId = 1,
                });

                var overwritten = repo.LoadSlot(91001, 0);
                Check("mercenary support state upserts by owner+slot", overwritten != null && overwritten.SkillId == 24 && overwritten.StrikerSkillId == 1);

                repo.Clear(91001, 0);
                Check("blank support selection clears persisted state", repo.LoadSlot(91001, 0) == null);
                Check("blank support selection disables subtype0 link", ReadSubtype0Link(tempDb, 91001) == "0/0/0");
                Check("blank support selection emits an empty 0x019F body",
                    StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()
                        .SequenceEqual(new byte[] { 0x00, 0x00 }));

                repo.Clear(91001, 0);
                Check("blank support selection clear is idempotent",
                    repo.LoadSlot(91001, 0) == null
                    && ReadSubtype0Link(tempDb, 91001) == "0/0/0");
            }
            finally
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    foreach (var path in new[] { tempDb, tempDb + "-wal", tempDb + "-shm" })
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                }
                catch { }
            }
        }

        private static string ReadSubtype0Link(string databasePath, int characterId)
        {
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(databasePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
SELECT link_slot_enabled, link_type_a, link_type_b
FROM character_subtype0_fields
WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;

                        return $"{reader.GetInt32(0)}/{reader.GetInt32(1)}/{reader.GetInt32(2)}";
                    }
                }
            }
        }

        private static void CheckTagRecordSerializerBoundary()
        {
            var snapshot = new UserInfoAdditionSnapshot
            {
                StatHpMax = 123456,
                StatMpMax = 65432,
                StatPhysicalAttack = 111,
                StatPhysicalDefense = 222,
                StatMagicalAttack = 333,
                StatMagicalDefense = 444,
                StatFireResistance = -5,
                StatWaterResistance = 6,
                StatDarkResistance = 7,
                StatLightResistance = 8,
                StatInventoryLimit = 987654,
                StatHpRegenSpeed = 11,
                StatMpRegenSpeed = 12,
                StatMoveSpeed = 13000,
                StatAttackSpeed = 14000,
                StatCastSpeed = 15000,
                StatHitRecovery = 16000,
                StatJumpPower = 17000,
                StatWeight = 765432,
                CloneTitleItemId = 456789,
                SkillTreeIndex = 1,
            };
            var avatar = ItemCore.Create(ItemCore.KindAvatar, 400001);
            var weapon = ItemCore.Create(ItemCore.KindEquipment, 12345);
            weapon.Value = 0x10203040;
            weapon.Durability = 77;
            ApplyTailFields(weapon, Enumerable.Range(1, 10).Select(i => (byte)i).ToArray());
            var lastClientSlot = ItemCore.Create(ItemCore.KindEquipment, 0x17E69F80);
            ApplyTailFields(lastClientSlot, Enumerable.Repeat((byte)0x5A, 10).ToArray());
            var equipment = new[]
            {
                CreateEquippedEntry(0, avatar),
                CreateEquippedEntry(11, weapon),
                CreateEquippedEntry(29, lastClientSlot),
            };
            var skillPage = new List<SkillInfoEntrySnapshot>
            {
                new SkillInfoEntrySnapshot { Slot = 54, SkillId = 33, Level = 10 },
                new SkillInfoEntrySnapshot { Slot = 198, SkillId = 72, Level = 23 },
                new SkillInfoEntrySnapshot { Slot = 199, SkillId = 73, Level = 21 },
            };
            var nameLengths = new[] { 1, 12 };
            var previousLength = -1;
            var previousNameLength = 0;
            foreach (var nameLength in nameLengths)
            {
                var name = Enumerable.Range(0, nameLength).Select(i => (byte)('A' + i % 26)).ToArray();
                var raw = StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                    1001, name, 86, 0, 0x21, 72, snapshot, equipment, skillPage);
                Check($"0x019F serializer preserves DSTR name length {nameLength}",
                    BitConverter.ToUInt16(raw, 0) == 1001 &&
                    BitConverter.ToInt32(raw, 2) == nameLength &&
                    raw.Skip(6).Take(nameLength).SequenceEqual(name));
                Check($"0x019F serializer record length follows DSTR length {nameLength}",
                    previousLength < 0 || raw.Length == previousLength + nameLength - previousNameLength);
                previousLength = raw.Length;
                previousNameLength = nameLength;
            }

            var record = StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                1001, new byte[] { 0x54, 0x45, 0x53, 0x54 }, 86, 0, 0x21, 72,
                snapshot, equipment, skillPage);
            var offset = 2;
            var nameSize = BitConverter.ToInt32(record, offset);
            offset += 4 + nameSize;
            Check("0x019F serializer preserves level/job/full grow header bytes",
                record[offset] == 86 && record[offset + 1] == 0 && record[offset + 2] == 0x21);
            offset += 3;
            Check("0x019F serializer preserves selected skill header", BitConverter.ToUInt16(record, offset) == 72);
            offset += 2;
            var statLength = BitConverter.ToInt32(record, offset);
            offset += 4;
            Check("0x019F serializer keeps 82B stat blob and zero-filled 34B opaque middle",
                statLength == 82 &&
                BitConverter.ToUInt32(record, offset) == snapshot.StatHpMax &&
                record.Skip(offset + 24).Take(34).All(value => value == 0) &&
                BitConverter.ToUInt32(record, offset + 78) == snapshot.StatWeight);
            offset += statLength;
            Check("0x019F serializer writes equipment count", record[offset++] == equipment.Length);
            foreach (var item in equipment)
            {
                var rawItem = BuildExpectedNoti2Entry(item, snapshot);
                Check($"0x019F serializer writes ItemCore bytes for synthetic slot {item.Slot}",
                    record.Skip(offset).Take(rawItem.Length).SequenceEqual(rawItem));
                offset += rawItem.Length;
            }
            Check("0x019F serializer places clone title after equipment",
                BitConverter.ToUInt32(record, offset) == snapshot.CloneTitleItemId);
            offset += 4;
            Check("0x019F serializer writes skill page count/index",
                record[offset] == skillPage.Count && record[offset + 1] == snapshot.SkillTreeIndex);
            var skillOffset = offset + 2;
            var expectedSkillBytes = skillPage.SelectMany(skill => new[]
            {
                skill.Slot,
                (byte)(skill.SkillId & 0xFF),
                (byte)(skill.SkillId >> 8),
                skill.Level,
            });
            Check("0x019F serializer preserves skill slot/id/level",
                expectedSkillBytes.SequenceEqual(record.Skip(skillOffset).Take(skillPage.Count * 4)));
            var opaqueTailOffset = skillOffset + skillPage.Count * 4;
            Check("0x019F serializer writes empty opaqueFlag/count/u32[] tail",
                record.Length == opaqueTailOffset + 5 &&
                record.Skip(opaqueTailOffset).SequenceEqual(new byte[5]));

            var emptyRecord = StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                1001, new byte[] { 0x54, 0x45, 0x53, 0x54 }, 86, 0, 0x21, 72,
                snapshot, Array.Empty<EquippedEntrySnapshot>(), Array.Empty<SkillInfoEntrySnapshot>());
            Check("0x019F serializer supports zero equipment and skill counts",
                emptyRecord.Length == 113 && emptyRecord[101] == 0 && emptyRecord[106] == 0 &&
                emptyRecord.Skip(108).SequenceEqual(new byte[5]));

            var maxSkillPage = Enumerable.Range(0, byte.MaxValue)
                .Select(i => new SkillInfoEntrySnapshot
                {
                    Slot = (byte)i,
                    SkillId = (ushort)(1000 + i),
                    Level = 1,
                })
                .ToList();
            var maxSkillRecord = StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                1001, new byte[] { 0x54, 0x45, 0x53, 0x54 }, 86, 0, 0x21, 72,
                snapshot, Array.Empty<EquippedEntrySnapshot>(), maxSkillPage);
            Check("0x019F serializer accepts the u8 maximum skill count",
                maxSkillRecord[106] == byte.MaxValue && maxSkillRecord.Length == 113 + byte.MaxValue * 4);

            var overflowRejected = false;
            try
            {
                StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                    1001, new byte[] { 0x54 }, 86, 0, 0x21, 72,
                    snapshot,
                    Array.Empty<EquippedEntrySnapshot>(),
                    maxSkillPage.Concat(new[] { new SkillInfoEntrySnapshot { Slot = 255, SkillId = 2000, Level = 1 } }).ToList());
            }
            catch (ArgumentOutOfRangeException)
            {
                overflowRejected = true;
            }
            Check("0x019F serializer rejects skill count overflow", overflowRejected);
        }

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name);
            if (!ok)
                _failures++;
        }

        private static EquippedEntrySnapshot CreateEquippedEntry(short slot, ItemCore core)
        {
            return new EquippedEntrySnapshot
            {
                Slot = slot,
                Core = core,
            };
        }

        private static byte[] BuildExpectedNoti2Entry(EquippedEntrySnapshot entry, UserInfoAdditionSnapshot snapshot)
        {
            var writer = new DfoServer.Network.GamePacketWriter();
            ItemListProtocolWriter.WriteNoti2EquippedEntry(
                writer,
                entry.Slot,
                entry.Core,
                snapshot.GetAvatarDetail(entry.Core),
                snapshot.GetCreatureDetail(entry.Core));
            return writer.ToArray();
        }

        private static void ApplyTailFields(ItemCore core, byte[] tail)
        {
            if (core == null || tail == null)
                return;

            if (tail.Length > 0)
                core.GenuineUpgrade = tail[0];
            if (tail.Length > 1)
                core.EmancipateEquipmentLevel = tail[1];
            if (tail.Length > 2)
                core.TradeRestriction = tail[2];
            if (tail.Length > 4)
                core.TailUnknown0 = BitConverter.ToUInt16(tail, 3);
            if (tail.Length > 5)
                core.TailUnknown1 = tail[5];
            if (tail.Length > 6)
                core.TailUnknown2 = tail[6];
            if (tail.Length > 7)
                core.TailUnknown3 = tail[7];
            if (tail.Length > 8)
                core.RemainUseCount = tail[8];
            if (tail.Length > 9)
                core.SortLockFlag = tail[9];
        }

        private sealed class FixedSelectCharacterDataSource : ISelectCharacterDataSource
        {
            private readonly SelectCharacterDataSnapshot _snapshot;

            public FixedSelectCharacterDataSource(SelectCharacterDataSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public SelectCharacterDataSnapshot Load(int characterId, int accountId) => _snapshot;
            public int GetSeedCharacterId() => 0;
            public void InitializeNewCharacter(int characterId, int accountId, byte job) { }
        }
    }
}
