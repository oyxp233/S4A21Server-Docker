using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mercenary;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Mercenary;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    // A21 冒险团 / 支援兵 / 佣兵：请求允许 padding；成功选择只发 0x019F。
    public static class A21AdventureGroupProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_ADVENTURE_GROUP_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "adventure-group opcodes come from PacketTypesA21",
                (ushort)CmdPacketTypeA21.REQUEST_CHARAC_SKILL_INFO == 0x01E5
                && (ushort)CmdPacketTypeA21.SELECT_STRIKER == 0x01E8
                && (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO == 0x019F
                && (ushort)CmdPacketTypeA21.MERCENARY_INFO == 0x01BA
                && (ushort)CmdPacketTypeA21.MERCENARY_COMPETITION == 0x01BB
                && (ushort)CmdPacketTypeA21.MERCENARY_RETURN == 0x01B9
                && (ushort)NotiPacketTypeA21.CERA == 0x0035,
                ref failures);

            CheckPaddedParsers(ref failures);
            CheckSkillListLayout(ref failures);
            CheckSkillPageWireCombo(ref failures);
            CheckTagCharacterRecordLayout(ref failures);
            CheckTagCharacterEquipmentSlots(ref failures);
            CheckPartySupportBody(ref failures);
            CheckCombatStatBlobIsShared(ref failures);
            CheckMercenaryWaitingAndReturn(ref failures);
            CheckInitSequencePushesMercenaryInfo(ref failures);
            CheckUserInfoSubtype6AndMoodValue(ref failures);
            CheckRosterWireIndex(ref failures);
            CheckHandlerRegistrationConstants(ref failures);
            CheckAdventureGroupUserInfoTail(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_ADVENTURE_GROUP_PROTOCOL: PASS"
                    : $"A21_ADVENTURE_GROUP_PROTOCOL: FAIL count={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckAdventureGroupUserInfoTail(ref int failures)
        {
            var characters = new[]
            {
                new CharacterRecord { CharacterId = 5461, AccountId = 1, Level = 86 },
                new CharacterRecord { CharacterId = 5462, AccountId = 1, Level = 86 },
            };
            var group = AdventureGroupDataProvider.Calculate(characters);
            var honor = new HonorLevelSummary { HonorLevel = 9, HonorExp = 321 };
            foreach (var populated in new[] { false, true })
            {
                var addition = new UserInfoAdditionSnapshot();
                SkillInfoSnapshot skills = null;
                if (populated)
                {
                    addition.SpecialRewardQuestIds.Add(0x34BE);
                    addition.SpecialRewardQuestIds.Add(0x34C0);
                    skills = new SkillInfoSnapshot();
                    for (var pageIndex = 0; pageIndex < 2; pageIndex++)
                    {
                        var page = new SkillInfoPageSnapshot();
                        page.Entries.Add(new SkillInfoEntrySnapshot { SkillId = 179, Level = 7 });
                        page.Entries.Add(new SkillInfoEntrySnapshot { SkillId = 174, Level = 1 });
                        skills.Pages.Add(page);
                    }
                }

                var body = UserInfoBroadcastService.BuildSubtype1Body(characters[0], addition, characters, honor, skills);
                using var stream = new MemoryStream(body);
                using var reader = new BinaryReader(stream);
                // Independent walk of 13E6370 and its packet-consuming callees.
                var valid = reader.ReadByte() == 1 && reader.ReadUInt16() == 1;
                reader.ReadUInt32();
                reader.ReadUInt16();
                valid &= reader.ReadUInt32() == honor.HonorLevel;
                valid &= reader.ReadUInt32() == honor.HonorExp;
                reader.ReadByte();
                valid &= reader.ReadUInt16() == characters[0].CharacterId;
                reader.ReadUInt32();
                var statLength = reader.ReadInt32();
                valid &= statLength == 88 && reader.ReadBytes(statLength).Length == statLength;
                reader.ReadByte();
                valid &= reader.ReadByte() == 0; // no equipment in this fixture
                reader.ReadBytes(12);
                reader.ReadByte();
                for (var pageIndex = 0; pageIndex < 2; pageIndex++)
                {
                    var count = reader.ReadByte();
                    valid &= count == (populated ? 2 : 0);
                    for (var index = 0; index < count; index++)
                    {
                        reader.ReadUInt16();
                        reader.ReadByte();
                    }
                }
                reader.ReadByte(); // creature level, immediately followed by counts
                var dimensionCount = reader.ReadByte();
                valid &= dimensionCount == 26;
                reader.ReadBytes(dimensionCount * 6);
                reader.ReadBytes(3);
                var accountCount = reader.ReadByte();
                valid &= accountCount == 0;
                reader.ReadBytes(accountCount * 8);
                reader.ReadByte();
                var questCount = reader.ReadUInt32();
                valid &= questCount == (populated ? 2u : 0u);
                for (var index = 0; index < questCount; index++)
                    reader.ReadUInt32();
                var manageLevel = reader.ReadByte();
                var managePoint = reader.ReadInt32();
                Check(
                    $"USERINFO1 preserves adventure-group buff level through native dynamic fields (populated={populated})",
                    valid && group.ManageLevel > 0 && manageLevel == group.ManageLevel
                    && managePoint == group.TotalPoint && stream.Position == stream.Length,
                    ref failures);
            }
        }

        private static void CheckPaddedParsers(ref int failures)
        {
            var skillInfo = new byte[16];
            skillInfo[0] = 0x02;
            Check(
                "0x01E5 accepts padded 16B body and keeps the wire echo",
                MercenaryCommandParser.TryParseSkillInfo(skillInfo, out var skillCommand)
                && skillCommand.WireSlot == 2
                && skillCommand.WireSlotEcho == 2,
                ref failures);
            Check(
                "0x01E5 rejects a body shorter than 2B",
                !MercenaryCommandParser.TryParseSkillInfo(new byte[1], out _),
                ref failures);

            var select = new byte[16];
            select[0] = 0x03;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)43), 0, select, 1, 2);
            Check(
                "0x01E8 accepts padded 16B body and reads skill id 43",
                MercenaryCommandParser.TryParseSelectStriker(select, out var selectCommand)
                && selectCommand.WireSlot == 3
                && selectCommand.SkillId == 43,
                ref failures);
            Check(
                "0x01E8 skill id 0 stays skill id 0",
                MercenaryCommandParser.TryParseSelectStriker(new byte[] { 0x01, 0x00, 0x00 }, out var zeroSkill)
                && zeroSkill.WireSlot == 1
                && zeroSkill.SkillId == 0,
                ref failures);
            Check(
                "0x01E8 rejects a body shorter than 3B",
                !MercenaryCommandParser.TryParseSelectStriker(new byte[2], out _),
                ref failures);

            var ret = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(0x092B), 0, ret, 1, 4);
            Check(
                "0x01B9 accepts padded 8B body and does not require exact length 5",
                MercenaryCommandParser.TryParseReturn(ret, out var returnCommand)
                && returnCommand.Purpose == 0
                && returnCommand.CharacterId == 0x092B,
                ref failures);
            Check(
                "0x01B9 rejects a body shorter than 5B",
                !MercenaryCommandParser.TryParseReturn(new byte[4], out _),
                ref failures);

            var competition = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(0x092B), 0, competition, 0, 4);
            competition[4] = 1;
            competition[5] = 2;
            Check(
                "0x01BB accepts padded 16B body and does not require exact length 6",
                MercenaryCommandParser.TryParseCompetition(competition, out var competitionCommand)
                && competitionCommand.CharacterId == 0x092B
                && competitionCommand.AreaIndex == 1
                && competitionCommand.PeriodIndex == 2,
                ref failures);
            Check(
                "0x01BB rejects a body shorter than 6B",
                !MercenaryCommandParser.TryParseCompetition(new byte[5], out _),
                ref failures);
        }

        private static void CheckSkillListLayout(ref int failures)
        {
            var skills = new[]
            {
                new StrikerSupportSkillWireEntry(0x0A, 43, 1),
                new StrikerSupportSkillWireEntry(0x0B, 51, 5),
            };
            var ack = StrikerSupportSkillListWriter.BuildSkillListSuccessAck(
                0x0002,
                job: 13,
                growType: 0x11,
                skills);
            Check(
                "0x01E5 success ACK writes echo/job/grow/combo instead of zeros",
                ack.Length == 6 + (StrikerSupportSkillListWriter.EntrySize * skills.Length)
                && ack[0] == 1
                && BitConverter.ToUInt16(ack, 1) == 2
                && ack[3] == 13
                && ack[4] == 0x11
                && ack[5] == 2
                && ack[6] == 0x0A
                && BitConverter.ToUInt16(ack, 7) == 43
                && ack[9] == 1
                && ack[10] == 0x0B
                && BitConverter.ToUInt16(ack, 11) == 51
                && ack[13] == 5,
                ref failures);
            Check(
                "0x01E5/0x01E8 failure ACK stays a 2-byte CMD failure",
                StrikerSupportSkillListWriter.BuildFailureAck().Length == 2
                && StrikerSupportSkillListWriter.BuildFailureAck()[0] == 0,
                ref failures);
        }

        private static void CheckSkillPageWireCombo(ref int failures)
        {
            var page = new[]
            {
                new SkillInfoEntrySnapshot { Slot = 102, SkillId = 179, Level = 7 },
                new SkillInfoEntrySnapshot { Slot = 4, SkillId = 43, Level = 0 },
                new SkillInfoEntrySnapshot { Slot = 12, SkillId = 28, Level = 1 },
            };
            var skills = StrikerSupportSkillListSource.FromSkillPage(page);
            Check(
                "0x01E5/0x019F combo is the skill-tree slot, including life-tree 102 and unlearned level 1",
                skills.Count == 3
                && skills[0].ComboIndex == 102
                && skills[0].SkillId == 179
                && skills[0].DisplayLevel == 7
                && skills[1].ComboIndex == 4
                && skills[1].SkillId == 43
                && skills[1].DisplayLevel == 1
                && skills[2].ComboIndex == 12
                && skills[2].SkillId == 28
                && skills[2].DisplayLevel == 1,
                ref failures);

            var priestLikeAck = StrikerSupportSkillListWriter.BuildSkillListSuccessAck(
                0x0004,
                job: 4,
                growType: 0x21,
                skills);
            Check(
                "success ACK writes skill-tree Slot as combo",
                priestLikeAck[0] == 1
                && priestLikeAck[3] == 4
                && priestLikeAck[4] == 0x21
                && priestLikeAck[5] == 3
                && priestLikeAck[6] == 102
                && priestLikeAck[14] == 12
                && BitConverter.ToUInt16(priestLikeAck, 15) == 28,
                ref failures);
        }

        private static void CheckTagCharacterRecordLayout(ref int failures)
        {
            var snapshot = new UserInfoAdditionSnapshot
            {
                StatHpMax = 1234,
                StatInventoryLimit = 60,
            };
            var skills = new[]
            {
                new StrikerSupportSkillWireEntry(0x0A, 43, 1),
            };
            var record = StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                0x1234,
                new byte[] { (byte)'n' },
                level: 70,
                job: 13,
                growType: 0x11,
                selectedSkillId: 43,
                snapshot,
                Array.Empty<EquippedEntrySnapshot>(),
                skills);

            var offset = 0;
            var cid = ReadUInt16(record, ref offset);
            var nameLength = ReadInt32(record, ref offset);
            offset += nameLength;
            var level = record[offset++];
            var job = record[offset++];
            var grow = record[offset++];
            var selectedSkill = ReadUInt16(record, ref offset);
            var statLen = ReadInt32(record, ref offset);
            var blobOffset = offset;
            offset += statLen;
            var equipCount = record[offset++];
            var cloneTitle = ReadUInt32(record, ref offset);
            var skillCount = record[offset++];
            var skillTree = record[offset++];
            var combo = record[offset++];
            var skillId = ReadUInt16(record, ref offset);
            var displayLevel = record[offset++];
            var opaquePrefix = record[offset++];
            var opaquePayload = ReadUInt32(record, ref offset);

            Check(
                "0x019F record uses the 88B combat blob, locked skill tree and 0x6F tail",
                cid == 0x1234
                && nameLength == 1
                && level == 70
                && job == 13
                && grow == 0x11
                && selectedSkill == 43
                && statLen == CombatStatBlobWriter.BlobLength
                && BitConverter.ToUInt32(record, blobOffset) == 1234
                && BitConverter.ToUInt16(record, blobOffset + 56) == CombatStatBlobWriter.MiddleMarker
                && equipCount == 0
                && cloneTitle == 0
                && skillCount == 1
                && skillTree == SkillTreeExpansionState.LockedWireValue
                && combo == 0x0A
                && skillId == 43
                && displayLevel == 1
                && opaquePrefix == StrikerSupportTagCharacterPacketBuilder.RecordOpaquePrefix
                && opaquePayload == 0
                && offset == record.Length,
                ref failures);
        }

        private static void CheckTagCharacterEquipmentSlots(ref int failures)
        {
            Check(
                "A21 0x019F equipment slots match USERINFO Noti2: 0-28 plus guild medal 31",
                EquipmentTypeInfo.IsA21Noti2EquippedSlot((short)EquipmentType.ArtifactGreen)
                && EquipmentTypeInfo.IsA21Noti2EquippedSlot((short)EquipmentType.GuildMedal)
                && !EquipmentTypeInfo.IsA21Noti2EquippedSlot((short)EquipmentType.NameTag)
                && !EquipmentTypeInfo.IsA21Noti2EquippedSlot((short)EquipmentType.Charm),
                ref failures);

            var snapshot = new UserInfoAdditionSnapshot();
            for (byte slot = 0; slot <= 8; slot++)
            {
                snapshot.EquippedEntries.Add(new EquippedEntrySnapshot
                {
                    Slot = slot,
                    Core = ItemCore.Create(ItemCore.KindAvatar, 1000 + slot),
                });
            }
            snapshot.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = (short)EquipmentType.Charm,
                Core = ItemCore.Create(ItemCore.KindEquipment, 2000),
            });
            snapshot.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = (short)EquipmentType.NameTag,
                Core = ItemCore.Create(ItemCore.KindEquipment, 2001),
            });
            snapshot.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = (short)EquipmentType.GuildMedal,
                Core = ItemCore.Create(ItemCore.KindGuildMedal, 100380044),
            });

            var ok = StrikerSupportTagCharacterPacketBuilder.TryBuildEquipmentListForTest(
                snapshot,
                job: 255,
                growType: 0,
                out var equipment,
                out var reason);
            Check(
                "support 0x019F keeps guild medal slot 31 and skips charm/name-tag so linking still succeeds",
                ok
                && reason == null
                && equipment != null
                && equipment.Any(entry => entry.Slot == (short)EquipmentType.GuildMedal
                    && entry.Core.ItemId == 100380044)
                && equipment.All(entry => entry.Slot != (short)EquipmentType.Charm)
                && equipment.All(entry => entry.Slot != (short)EquipmentType.NameTag),
                ref failures);

            var medalOnly = new[]
            {
                new EquippedEntrySnapshot
                {
                    Slot = (short)EquipmentType.GuildMedal,
                    Core = ItemCore.Create(ItemCore.KindGuildMedal, 100380044),
                },
            };
            var record = StrikerSupportTagCharacterPacketBuilder.BuildRecordForTest(
                0x1234,
                new byte[] { (byte)'n' },
                level: 70,
                job: 13,
                growType: 0x11,
                selectedSkillId: 43,
                new UserInfoAdditionSnapshot(),
                medalOnly,
                new[] { new StrikerSupportSkillWireEntry(0x0A, 43, 1) });
            var offset = 2 + 4 + 1 + 3 + 2 + 4 + CombatStatBlobWriter.BlobLength;
            Check(
                "0x019F writes guild medal as Noti2 slot 31 before the skill table",
                record[offset] == 1
                && record[offset + 1] == (byte)EquipmentType.GuildMedal
                && BitConverter.ToInt32(record, offset + 2) == 100380044,
                ref failures);
        }

        private static void CheckPartySupportBody(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                "s4a21-party-support-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                Check(
                    "party 0x019F rejects null/empty owner list",
                    !StrikerSupportTagCharacterPacketBuilder.TryBuildPartySupportBody(
                        null, database, out var nullBody)
                    && nullBody == null
                    && !StrikerSupportTagCharacterPacketBuilder.TryBuildPartySupportBody(
                        Array.Empty<int>(), database, out var emptyBody)
                    && emptyBody == null,
                    ref failures);
                Check(
                    "party 0x019F skips invalid cids and owners without support state",
                    !StrikerSupportTagCharacterPacketBuilder.TryBuildPartySupportBody(
                        new[] { 0, -7, 424240, 424241 }, database, out var missingBody)
                    && missingBody == null,
                    ref failures);

                var entry = StrikerSkillDataProvider.GetAll()
                    .Where(candidate => candidate.RequiredLevel <= 86)
                    .OrderBy(candidate => candidate.RequiredLevel)
                    .FirstOrDefault();
                var minimumLevel = StrikerSkillDataProvider.GetMinimumSupportLevel();
                Check(
                    "PVF striker data supports the party 0x019F fixture",
                    entry != null
                    && minimumLevel > 0
                    && minimumLevel <= 86
                    && StrikerSkillDataProvider.GetMaxActiveSupportCount() == 1,
                    ref failures);
                if (entry == null || minimumLevel <= 0 || minimumLevel > 86)
                    return;

                const int owner1 = 41011;
                const int owner2 = 41012;
                const int owner3 = 41013;
                const int support = 42020;
                const int crossAccountSupport = 42021;
                var supportLevel = (byte)Math.Max(minimumLevel, entry.RequiredLevel);
                var supportGrow = (byte)(0x10 | entry.GrowType);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (4477, 'party-support-fixture', ''), (4488, 'party-support-other', '');
INSERT INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (@owner1, 4477, 'po1', 0, 0, 86),
       (@owner2, 4477, 'po2', 0, 0, 86),
       (@owner3, 4477, 'po3', 0, 0, 86),
       (@support, 4477, 'psup', @job, @grow, @level),
       (@crossSupport, 4488, 'pxsup', @job, @grow, @level);
INSERT INTO character_subtype1_fields (character_id) VALUES (@support);";
                    command.Parameters.AddWithValue("@owner1", owner1);
                    command.Parameters.AddWithValue("@owner2", owner2);
                    command.Parameters.AddWithValue("@owner3", owner3);
                    command.Parameters.AddWithValue("@support", support);
                    command.Parameters.AddWithValue("@crossSupport", crossAccountSupport);
                    command.Parameters.AddWithValue("@job", entry.Job);
                    command.Parameters.AddWithValue("@grow", supportGrow);
                    command.Parameters.AddWithValue("@level", supportLevel);
                    command.ExecuteNonQuery();
                }

                var repository = new SqliteMercenarySupportRepository(database);
                repository.Save(new MercenarySupportState
                {
                    OwnerCharacterId = owner1,
                    Slot = MercenarySupportState.SingletonStateKey,
                    SupportCharacterId = support,
                    SkillId = (ushort)entry.SkillIndex,
                    StrikerSkillId = (ushort)entry.ComboIndex,
                });
                repository.Save(new MercenarySupportState
                {
                    OwnerCharacterId = owner2,
                    Slot = MercenarySupportState.SingletonStateKey,
                    SupportCharacterId = support,
                    SkillId = (ushort)entry.SkillIndex,
                    StrikerSkillId = (ushort)entry.ComboIndex,
                });
                // owner3 的支援角色属于另一个账号：状态校验必须失败但只跳过该成员。
                repository.Save(new MercenarySupportState
                {
                    OwnerCharacterId = owner3,
                    Slot = MercenarySupportState.SingletonStateKey,
                    SupportCharacterId = crossAccountSupport,
                    SkillId = (ushort)entry.SkillIndex,
                    StrikerSkillId = (ushort)entry.ComboIndex,
                });

                var owner1State = repository.LoadSlot(
                    owner1,
                    MercenarySupportState.SingletonStateKey);
                var owner1Body = StrikerSupportTagCharacterPacketBuilder.BuildOwnerMappedBody(
                    owner1,
                    owner1State,
                    database);
                var singleBuilt = StrikerSupportTagCharacterPacketBuilder.TryBuildPartySupportBody(
                    new[] { owner1 },
                    database,
                    out var singleBody);
                Check(
                    "single-owner party 0x019F body stays byte-identical to BuildOwnerMappedBody",
                    singleBuilt
                    && singleBody != null
                    && owner1Body != null
                    && owner1Body.Length > 2
                    && singleBody.SequenceEqual(owner1Body),
                    ref failures);

                var owner2State = repository.LoadSlot(
                    owner2,
                    MercenarySupportState.SingletonStateKey);
                var owner2Body = StrikerSupportTagCharacterPacketBuilder.BuildOwnerMappedBody(
                    owner2,
                    owner2State,
                    database);
                var recordLength = owner1Body.Length - 2;
                var partyBuilt = StrikerSupportTagCharacterPacketBuilder.TryBuildPartySupportBody(
                    new[] { owner2, owner1 },
                    database,
                    out var partyBody);
                Check(
                    "party 0x019F aggregates count=N records keyed by owner cid in input order",
                    partyBuilt
                    && partyBody != null
                    && BitConverter.ToUInt16(partyBody, 0) == 2
                    && BitConverter.ToUInt16(partyBody, 2) == owner2
                    && partyBody.Length == 2 + (owner2Body.Length - 2) + recordLength
                    && partyBody.AsSpan(2, owner2Body.Length - 2)
                        .SequenceEqual(owner2Body.AsSpan(2))
                    && BitConverter.ToUInt16(partyBody, 2 + (owner2Body.Length - 2)) == owner1
                    && partyBody.AsSpan(2 + (owner2Body.Length - 2), recordLength)
                        .SequenceEqual(owner1Body.AsSpan(2)),
                    ref failures);

                var mixedBuilt = StrikerSupportTagCharacterPacketBuilder.TryBuildPartySupportBody(
                    new[] { 0, -1, owner1, owner1, owner3, owner2 },
                    database,
                    out var mixedBody);
                var expectedMixed = new byte[2 + recordLength + (owner2Body.Length - 2)];
                expectedMixed[0] = 2;
                expectedMixed[1] = 0;
                Array.Copy(owner1Body, 2, expectedMixed, 2, recordLength);
                Array.Copy(
                    owner2Body,
                    2,
                    expectedMixed,
                    2 + recordLength,
                    owner2Body.Length - 2);
                Check(
                    "party 0x019F dedupes owners, skips invalid cids and failed members",
                    mixedBuilt
                    && mixedBody != null
                    && mixedBody.SequenceEqual(expectedMixed),
                    ref failures);
            }
            finally
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    foreach (var path in new[] { tempDbPath, tempDbPath + "-wal", tempDbPath + "-shm" })
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                }
                catch
                {
                }
            }
        }

        private static void CheckCombatStatBlobIsShared(ref int failures)
        {
            var addition = new UserInfoAdditionSnapshot { StatHpMax = 9 };
            var blob = CombatStatBlobWriter.Build(addition);
            var userInfo1 = UserInfoSubtype1Builder.BuildFromSnapshot(addition, null);
            Check(
                "USERINFO subtype1 and 0x019F share the 88B combat stat blob",
                blob.Length == 88
                && CombatStatBlobWriter.BlobLength == 88
                && BitConverter.ToInt32(userInfo1, 4) == 88
                && userInfo1.AsSpan(8, 88).SequenceEqual(blob),
                ref failures);
        }

        private static void CheckMercenaryWaitingAndReturn(ref int failures)
        {
            var snapshot = new MercenaryInfoSnapshot
            {
                ManageLevel = 4,
                ManagePoint = 120,
            };
            snapshot.Records.Add(new MercenaryCharacterInfo
            {
                CharacterId = 42,
                Name = new byte[] { (byte)'a' },
                State = MercenaryExpeditionState.Waiting,
                RemainingSeconds = 0,
                AreaIndex = MercenaryCharacterInfo.WaitingAreaIndex,
                PeriodIndex = MercenaryCharacterInfo.UnassignedPeriodIndex,
            });
            var info = MercenaryExpeditionBodyBuilder.BuildInfoSuccess(snapshot);
            var offset = 0;
            var success = info[offset++];
            var manageLevel = info[offset++];
            var managePoint = ReadInt32(info, ref offset);
            var count = info[offset++];
            var cid = ReadInt32(info, ref offset);
            var nameLength = ReadInt32(info, ref offset);
            offset += nameLength;
            var state = info[offset++];
            var remaining = ReadInt32(info, ref offset);
            var area = info[offset++];
            var period = info[offset++];
            var avatar = info[offset++];
            Check(
                "MERCENARY_INFO waiting records use remaining=0 area=0 period=0xFF",
                success == 1
                && manageLevel == 4
                && managePoint == 120
                && count == 1
                && cid == 42
                && state == (byte)MercenaryExpeditionState.Waiting
                && remaining == 0
                && area == 0
                && period == 0xFF
                && avatar == 0
                && offset == info.Length
                && MercenaryCharacterInfo.WaitingAreaIndex == 0
                && MercenaryCharacterInfo.UnassignedPeriodIndex == 0xFF,
                ref failures);

            var empty = MercenaryExpeditionBodyBuilder.BuildInfoSuccess(new MercenaryInfoSnapshot());
            Check(
                "empty MERCENARY_INFO success list is still a cmd=1 body",
                empty.Length == 7
                && empty[0] == 1
                && empty[1] == 0
                && BitConverter.ToInt32(empty, 2) == 0
                && empty[6] == 0,
                ref failures);

            var cmdBuilder = new MercenaryInfoCmdBodyBuilder();
            Check(
                "init CMD builder emits empty MERCENARY_INFO when no account is selected",
                cmdBuilder.CmdType == (ushort)CmdPacketTypeA21.MERCENARY_INFO
                && cmdBuilder.TryBuild(null, out var initBody)
                && initBody != null
                && initBody.AsSpan().SequenceEqual(empty),
                ref failures);

            var returnAck = MercenaryExpeditionBodyBuilder.BuildReturnSuccess(0x092B, 0, 0, false);
            Check(
                "MERCENARY_RETURN success ACK writes status=2 instead of echoing purpose",
                returnAck.Length == 15
                && returnAck[0] == 1
                && returnAck[1] == MercenaryExpeditionBodyBuilder.ReturnSuccessStatus
                && MercenaryExpeditionBodyBuilder.ReturnSuccessStatus == 2
                && BitConverter.ToInt32(returnAck, 2) == 0x092B
                && BitConverter.ToInt32(returnAck, 6) == 0
                && BitConverter.ToInt32(returnAck, 10) == 0
                && returnAck[14] == 0,
                ref failures);
        }

        private static void CheckInitSequencePushesMercenaryInfo(ref int failures)
        {
            var sequence = NewCharacterInitSequence.Build();
            var mercenaryIndex = sequence.FindIndex(packet =>
                packet.Command == 0x01
                && packet.Type == (ushort)CmdPacketTypeA21.MERCENARY_INFO);
            var ceraIndex = sequence.FindIndex(packet =>
                packet.Command == 0x00
                && packet.Type == (ushort)NotiPacketTypeA21.CERA);
            var tagIndex = sequence.FindIndex(packet =>
                packet.Command == 0x00
                && packet.Type == (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO);
            Check(
                "select-character init pushes cmd=1 MERCENARY_INFO immediately before CERA",
                mercenaryIndex >= 0
                && ceraIndex == mercenaryIndex + 1
                && tagIndex >= 0
                && tagIndex < mercenaryIndex,
                ref failures);

            var userInfoOcc2 = sequence.FindIndex(packet =>
                packet.Command == 0x00
                && packet.Type == (ushort)NotiPacketTypeA21.USERINFO
                && packet.OccurrenceIndex == 2);
            Check(
                "select-character init places USERINFO occ=2 immediately before MERCENARY_INFO",
                userInfoOcc2 >= 0
                && userInfoOcc2 + 1 == mercenaryIndex,
                ref failures);

            var registry = new InitPacketBuilderRegistry();
            Check(
                "init registry serves MERCENARY_INFO through the CMD builder table",
                registry.TryBuildCmd(
                    (ushort)CmdPacketTypeA21.MERCENARY_INFO,
                    new SelectCharacterDataSnapshot(),
                    out var body)
                && body != null
                && body.Length == 7
                && body[0] == 1,
                ref failures);
        }

        private static void CheckUserInfoSubtype6AndMoodValue(ref int failures)
        {
            var subtype6 = UserInfoSubtype6Builder.BuildNotificationBody(0x092B);
            Check(
                "USERINFO subtype 6 is 25B",
                subtype6.Length == UserInfoSubtype6Builder.BodyLength
                && subtype6[0] == UserInfoSubtype6Builder.Subtype
                && BitConverter.ToUInt16(subtype6, 1) == UserInfoSubtype6Builder.Version
                && BitConverter.ToUInt16(subtype6, 3) == 0x092B
                && BitConverter.ToUInt32(subtype6, 5) == UserInfoSubtype6Builder.UnknownAllBits
                && BitConverter.ToUInt32(subtype6, 9) == UserInfoSubtype6Builder.SharedOpaqueConstant
                && BitConverter.ToUInt32(subtype6, 13) == UserInfoSubtype6Builder.TownReadyFlag
                && BitConverter.ToUInt32(subtype6, 17) == UserInfoSubtype6Builder.TownReadyFlag
                && BitConverter.ToUInt32(subtype6, 21) == 0,
                ref failures);

            var snapshot = new SelectCharacterDataSnapshot
            {
                CharacterRecord = new CharacterRecord
                {
                    CharacterId = 0x092B,
                    Name = new byte[] { (byte)'a' },
                },
            };
            var userInfo = new UserInfoBodyBuilder();
            Check(
                "USERINFO occ=2 builds subtype 6 instead of a second USERINFO0",
                userInfo.TryBuild(snapshot, 2, out var occ2)
                && occ2 != null
                && occ2.AsSpan().SequenceEqual(subtype6)
                && userInfo.TryBuild(snapshot, 0, out var occ0)
                && occ0 != null
                && occ0[0] == 0
                && occ0.Length > UserInfoSubtype6Builder.BodyLength,
                ref failures);

            var userInfo0 = UserInfoSubtype0Builder.BuildNotificationBody(snapshot.CharacterRecord);
            var moodOffset = userInfo0.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength
                + UserInfoSubtype0Builder.A21AfterAliveMoodValueOffset;
            Check(
                "USERINFO0 writes MoodValue at 64B-tail +59",
                moodOffset >= 0
                && moodOffset + 1 < userInfo0.Length
                && BitConverter.ToUInt16(userInfo0, moodOffset) == 0,
                ref failures);
        }

        private static void CheckRosterWireIndex(ref int failures)
        {
            var roster = new List<CharacterRecord>
            {
                new CharacterRecord { CharacterId = 11, Job = 1, Level = 70 },
                new CharacterRecord { CharacterId = 22, Job = 13, GrowType = 0x11, Level = 70 },
            };
            Check(
                "support wire index is the account roster subscript",
                StrikerSupportRoster.FindByWireIndex(roster, 1)?.CharacterId == 22
                && StrikerSupportRoster.FindByWireIndex(roster, 0)?.CharacterId == 11
                && StrikerSupportRoster.FindByWireIndex(roster, 2) == null,
                ref failures);
            Check(
                "town clear is selecting the current character",
                StrikerSupportRoster.IsTownClearSelection(roster[0], 11)
                && !StrikerSupportRoster.IsTownClearSelection(roster[1], 11)
                && !StrikerSupportRoster.IsTownClearSelection(null, 11)
                && !StrikerSupportRoster.IsEligibleSupport(roster[0], 11)
                && StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody().Length == 2
                && StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()[0] == 0
                && StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()[1] == 0,
                ref failures);
        }

        private static void CheckHandlerRegistrationConstants(ref int failures)
        {
            Check(
                "mercenary expedition handler commands stay on the A21 CMD enums",
                MercenaryExpeditionHandler.ReturnCommand == (ushort)CmdPacketTypeA21.MERCENARY_RETURN
                && MercenaryExpeditionHandler.InfoCommand == (ushort)CmdPacketTypeA21.MERCENARY_INFO
                && MercenaryExpeditionHandler.CompetitionCommand == (ushort)CmdPacketTypeA21.MERCENARY_COMPETITION,
                ref failures);
        }

        private static ushort ReadUInt16(byte[] body, ref int offset)
        {
            var value = BitConverter.ToUInt16(body, offset);
            offset += 2;
            return value;
        }

        private static int ReadInt32(byte[] body, ref int offset)
        {
            var value = BitConverter.ToInt32(body, offset);
            offset += 4;
            return value;
        }

        private static uint ReadUInt32(byte[] body, ref int offset)
        {
            var value = BitConverter.ToUInt32(body, offset);
            offset += 4;
            return value;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
