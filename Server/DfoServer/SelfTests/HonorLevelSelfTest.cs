using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Skills;
using System;
using System.IO;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class HonorLevelSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== HONOR_LEVEL selftest ===");
            try
            {
                var accountChars = new[]
                {
                    new CharacterRecord { CharacterId = 1, Level = 85, Exp = 500000000u },
                    new CharacterRecord { CharacterId = 2, Level = 86, Exp = 0u },
                    new CharacterRecord { CharacterId = 3, Level = 86, Exp = 10000000u },
                    new CharacterRecord { CharacterId = 4, Level = 86, Exp = 25000000u },
                    new CharacterRecord { CharacterId = 5, Level = 86, Exp = 10000000u, Deleted = true },
                };
                var mixed = HonorLevelDataProvider.CalculateFromHonorExp(35000000u, accountChars);
                Check("honor total exp comes from account honor progress, not character total exp", mixed.TotalHonorExp == 35000000u);
                Check("honor exp is current level segment exp", mixed.HonorExp == 5000000u);
                Check("honor level uses PVF segment requirements", mixed.HonorLevel == 3);
                Check("honor grade maps through PVF grade sections", mixed.HonorGrade == 1);
                Check("full-level count ignores deleted and non-max characters", mixed.FullLevelCharacterCount == 3);

                var capped = HonorLevelDataProvider.CalculateFromHonorExp(ulong.MaxValue, new[]
                {
                    new CharacterRecord { CharacterId = 6, Level = 86, Exp = uint.MaxValue },
                });
                Check("honor total exp is capped by summed PVF segment requirements", capped.TotalHonorExp == HonorLevelDataProvider.MaxTotalHonorExp);
                Check("honor current exp is capped by PVF [maxexp on maxlevel]", capped.HonorExp == (uint)HonorLevelDataProvider.MaxExpOnMaxLevel);
                Check("capped honor reaches PVF max level", capped.HonorLevel == 59);
                Check("capped honor reaches PVF max grade", capped.HonorGrade == 6);

                Check("honor exp gained while already max level",
                    HonorLevelDataProvider.CalculateHonorExpGain(86, 123456u, 1000u) == 1000u);
                var maxEntryExp = (uint)DfoServer.Game.Dungeon.ExpTableProvider.GetLevelThreshold(DfoServer.Game.Dungeon.ExpTableProvider.MaxLevel - 1);
                Check("only overflow becomes honor exp when reaching max level",
                    HonorLevelDataProvider.CalculateHonorExpGain(85, maxEntryExp - 100u, 250u) == 150u);


                var tempDb = Path.Combine(Path.GetTempPath(), "dfo_honor_selftest_" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    var repo = new HonorLevelProgressRepository(tempDb, ServerPaths.SchemaFilePath);
                    var connStr = SqliteDatabaseBootstrap.BuildConnectionString(tempDb);
                    using (var conn = new SqliteConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
INSERT INTO accounts(account_id, m_id) VALUES(100, 'honor_selftest');
INSERT INTO characters(character_id, account_id, name, level) VALUES(10001, 100, 'honor_char', 86);
INSERT INTO character_subtype1_fields(character_id, progress1, progress2) VALUES(10001, 59, 123456789);";
                            cmd.ExecuteNonQuery();
                        }
                    }

                    var emptyAccount = repo.LoadSummary(100);
                    Check("account honor repository reads no character progress as honor source", emptyAccount.TotalHonorExp == 0 && emptyAccount.HonorLevel == 1);
                    var afterAdd = repo.AddHonorExp(100, 273u);
                    Check("account honor repository stores account scoped total exp", afterAdd.TotalHonorExp == 273u && afterAdd.HonorExp == 273u);
                    using (var conn = new SqliteConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT honor_exp FROM accounts WHERE account_id=100;";
                            Check("accounts.honor_exp is the persisted honor source", Convert.ToInt64(cmd.ExecuteScalar()) == 273L);
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
                }

                var body = HonorLevelPacketBuilder.BuildInfoBody(mixed);
                Check("HONOR_LEVEL_INFO body is 8 bytes", body.Length == 8);
                Check("HONOR_LEVEL_INFO first u32 is honor level", BitConverter.ToUInt32(body, 0) == mixed.HonorLevel);
                Check("HONOR_LEVEL_INFO second u32 is honor exp", BitConverter.ToUInt32(body, 4) == mixed.HonorExp);

                var expBody = ExpNotificationBuilder.Build(
                    86, 0x11223344u,
                    new SkillPointProtocolState
                    {
                        Page0Sp = 0x5566,
                        Page1Sp = 0x6677,
                        Page0Tp = 0x7788,
                        Page1Tp = 0x8899,
                    },
                    mixed,
                    pvpVictoryPointSnapshot: 0x15161718u,
                    partyRewardExp: 0x01020304u,
                    memberRewardExp: 0x11121314u,
                    fatigueBuffBonusExp: 0x21222324u,
                    seriaBlessingBonusExp: 0x31323334u,
                    growthContractBonusExp: 0x41424344u,
                    fatigueBurnBonusExp: 0x51525354u,
                    internetCafeBonusExp: 0x61626364u,
                    eliteMonsterKillBonusExp: 0x71727374u,
                    growthCapsuleExp: 0x81828384u);
                Check("0x0025 keeps the current 95-byte N=0 output", expBody.Length == ExpNotificationBuilder.BodyLength);
                Check("0x0025 writes both skill-page SP and TP fields",
                    BitConverter.ToUInt16(expBody, 13) == 0x5566
                    && BitConverter.ToUInt16(expBody, 15) == 0x6677
                    && BitConverter.ToUInt16(expBody, 17) == 0x7788
                    && BitConverter.ToUInt16(expBody, 19) == 0x8899);
                Check("0x0025 writes the PvP victory-point snapshot at +0x15",
                    BitConverter.ToUInt32(expBody, ExpNotificationBuilder.PvpVictoryPointOffset) == 0x15161718u);
                Check("0x0025 writes zero variable entries in the current builder",
                    expBody[ExpNotificationBuilder.VariableEntryCountOffset] == 0);
                Check("0x0025 writes growth-capsule EXP at client offset +0x3B",
                    BitConverter.ToUInt32(expBody, ExpNotificationBuilder.GrowthCapsuleExpOffset) == 0x81828384u);
                Check("0x0025 writes honor level at client offset +0x3F",
                    BitConverter.ToUInt32(expBody, ExpNotificationBuilder.HonorLevelOffset) == mixed.HonorLevel);
                Check("0x0025 writes current honor-segment EXP at client offset +0x43",
                    BitConverter.ToUInt32(expBody, ExpNotificationBuilder.HonorExpOffset) == mixed.HonorExp);
                Check("0x0025 keeps the project-only unread 8-byte compatibility tail",
                    ExpNotificationBuilder.ClientReadLengthWithNoVariableEntries == 87
                    && ExpNotificationBuilder.CompatibilityTailLength == 8
                    && BitConverter.ToUInt32(expBody, 87) == 0
                    && BitConverter.ToUInt32(expBody, 91) == 0);
                Check("0x0025 matches the exact current 95-byte N=0 output",
                    BitConverter.ToString(expBody) ==
                    "56-44-33-22-11-04-03-02-01-14-13-12-11-66-55-77-66-88-77-99-88-" +
                    "18-17-16-15-24-23-22-21-00-34-33-32-31-44-43-42-41-00-00-00-00-" +
                    "54-53-52-51-64-63-62-61-00-74-73-72-71-00-00-00-00-84-83-82-81-" +
                    "03-00-00-00-40-4B-4C-00-00-00-00-00-00-00-00-00-00-00-00-00-" +
                    "00-00-00-00-00-00-00-00-00-00-00-00");

                var missingSkillPages = SkillStateService.GetProtocolState(
                    new SkillInfoSnapshot
                    {
                        Tail0 = 7,
                        Tail1 = 10770,
                        HasTailValues = true,
                    },
                    new SkillPointState { RemainingSp = 321, RemainingTp = 7, RemainingTpPage1 = 3 });
                // 四池独立: Page1Tp 来自派生的 PVP 树 TP, 绝不回读镜像 Tail1。
                Check("missing skill pages do not copy page0 SP and legacy Tail1 is never used as TP",
                    missingSkillPages.Page0Sp == 321
                    && missingSkillPages.Page1Sp == 0
                    && missingSkillPages.Page0Tp == 7
                    && missingSkillPages.Page1Tp == 3);

                var ordinaryExpBody = ExpNotificationBuilder.Build(
                    86, 0, default, mixed);
                Check("ordinary EXP producers default an unavailable PvP victory-point snapshot to zero",
                    BitConverter.ToUInt32(ordinaryExpBody, ExpNotificationBuilder.PvpVictoryPointOffset) == 0);

                var addition = new UserInfoAdditionSnapshot { ManageLevel = 4, FlagByte = 4 };
                HonorLevelDataProvider.ApplyToUserInfoAddition(addition, capped);
                Check("honor sync writes subtype1 progress1 as honor level", addition.Progress1 == capped.HonorLevel);
                Check("honor sync writes subtype1 progress2 as honor exp", addition.Progress2 == capped.HonorExp);
                Check("honor sync does not touch adventure manage level", addition.ManageLevel == 4 && addition.FlagByte == 4);

                var tail = new UserInfoMinimumTailSnapshot();
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, capped);
                Check("honor sync writes subtype0 progressA as honor level", tail.ProgressA == capped.HonorLevel);
                Check("honor sync writes subtype0 progressB as honor exp", tail.ProgressB == capped.HonorExp);

                var rosterBody = AccountCharacterListBodyBuilder.Build(new[]
                {
                    new CharacterRecord { CharacterId = 8, Name = new byte[] { (byte)'a' }, Job = 1, GrowType = 0, Level = 86 },
                    new CharacterRecord { CharacterId = 9, Name = new byte[] { (byte)'b' }, Job = 2, GrowType = 0, Level = 1 },
                }, new GetUserInfoTemplate { GateOrCount1 = 32, GateOrCount2 = 32 }, out _, mixed);
                var rosterNeedle = new byte[]
                {
                    mixed.HonorLevel, 0, 0, 0,
                    (byte)(mixed.HonorExp & 0xFF), (byte)((mixed.HonorExp >> 8) & 0xFF),
                    (byte)((mixed.HonorExp >> 16) & 0xFF), (byte)((mixed.HonorExp >> 24) & 0xFF),
                    0, 0
                };
                var firstRosterHonor = IndexOf(rosterBody, rosterNeedle);
                Check("roster subtype2 writes shared honor display fields", firstRosterHonor >= 0);
                Check("roster subtype2 writes shared honor for every listed character", IndexOf(rosterBody, rosterNeedle, firstRosterHonor + 1) >= 0);

                var roundTripRecord = new CharacterRecord { CharacterId = 7, Name = new byte[] { (byte)'x' }, Job = 0, Level = 86, Subtype0Tail = tail };
                var roundTripBody = UserInfoSubtype0Builder.BuildNotificationBody(roundTripRecord);
                var tailOffset = roundTripBody.Length - UserInfoMinimumTailSnapshot.TailLength;
                var parsedTail = UserInfoMinimumTailSnapshot.FromBytes(roundTripBody.AsSpan(tailOffset, UserInfoMinimumTailSnapshot.TailLength).ToArray());
                Check("subtype0 builder keeps honor level at progressA offset", parsedTail.ProgressA == capped.HonorLevel);
                Check("subtype0 builder keeps honor exp at progressB offset", parsedTail.ProgressB == capped.HonorExp);

                Console.WriteLine("HonorLevelSelfTest OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("HonorLevelSelfTest FAILED: " + ex.Message);
                return 1;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
        {
            if (haystack == null || needle == null || needle.Length == 0)
                return -1;
            for (var i = Math.Max(0, start); i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        private static void Check(string name, bool condition)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            Console.WriteLine("  PASS " + name);
        }
    }
}
