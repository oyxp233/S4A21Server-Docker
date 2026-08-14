using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DailyChallengeSelfTest
    {
        private const int AccountId = 986026;
        private const int CharacterId = 986126;
        private const ushort ChallengeQuestId = 14653;
        private const ushort NormalQuestId = 1791;

        public static int Run()
        {
            Console.WriteLine("=== DAILY_CHALLENGE selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "daily-challenge.db");
            DeleteDatabase(databasePath);

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(connectionString);

            var failures = 0;
            var triggerSessionId = Guid.NewGuid();
            InventoryContext.Register(
                triggerSessionId,
                new InventoryService(CharacterId, AccountId));
            Check("PVF classifies configured challenge quest",
                QuestData.IsDailyChallengeQuest(ChallengeQuestId),
                ref failures);
            Check("normal active quest is not classified as daily challenge",
                !QuestData.IsDailyChallengeQuest(NormalQuestId),
                ref failures);

            var selectInit = new Game.SelectCharacter.SelectCharacterInitializationSnapshot
            {
                DailyChallengeCharacterLevel = 7,
            };
            var initialGroup = new Game.SelectCharacter.RacingDungeonGroupSnapshot
            {
                GroupId = 5,
            };
            initialGroup.Entries.Add(new Game.SelectCharacter.RacingDungeonEntrySnapshot
            {
                TrackLikeId = ChallengeQuestId,
                ValueA = 3,
                ValueB = 3,
            });
            selectInit.RacingDungeonGroups.Add(initialGroup);
            selectInit.RacingDungeonTailIds.Add(777);
            var selectSnapshot = new Game.SelectCharacter.SelectCharacterDataSnapshot
            {
                InitializationSnapshot = selectInit,
                CharacterRecord = new Game.Characters.CharacterRecord { Level = 86 },
            };
            new DailyChallengeBodyBuilder().TryBuild(selectSnapshot, 0, out var selectBody);
            Check("selection 0x0286 uses the current character level instead of the legacy seed",
                BitConverter.ToUInt32(selectBody, 0) == 86,
                ref failures);
            Check("initial 0x0286 entry uses remaining,target wire order (3,3)",
                IsExpectedSnapshot(selectBody, remaining: 3),
                ref failures);

            var firstSender = new RecordingSender();
            var firstManager = new QuestManager(firstSender, connectionString);
            firstManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();

            Check("first challenge event persists 3 -> 2",
                ReadChallengeValue(connectionString) == 2,
                ref failures);
            Check("challenge SET_TRIGGER sends only the full 0x0286 snapshot",
                firstSender.Calls.Count == 1
                && firstSender.Calls[0] == "NOTI:0286",
                ref failures);
            Check("challenge SET_TRIGGER does not emit a 0x0021 ACK",
                firstSender.LastAckBody == null,
                ref failures);
            Check("in-progress 0x0286 entry uses remaining,target wire order (2,3)",
                IsExpectedSnapshot(firstSender.LastNotiBody, remaining: 2),
                ref failures);

            var rebuiltSender = new RecordingSender();
            var rebuiltManager = new QuestManager(rebuiltSender, connectionString);
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("rebuilt service reads persisted value and applies 2 -> 1",
                ReadChallengeValue(connectionString) == 1
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && rebuiltSender.LastAckBody == null,
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("third challenge event persists 1 -> 0",
                ReadChallengeValue(connectionString) == 0
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && rebuiltSender.LastAckBody == null,
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("completed 0x0286 entry uses remaining,target wire order (0,3)",
                ReadChallengeValue(connectionString) == 0
                && rebuiltSender.LastAckBody == null
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && IsExpectedSnapshot(rebuiltSender.LastNotiBody, remaining: 0),
                ref failures);

            var resetService = new DailyChallengeService(connectionString);
            var reset = resetService.ResetCharacter(CharacterId);
            Check("daily reset restores all remaining values from their targets",
                reset.ChangedEntries == 1
                && ReadChallengeValue(connectionString) == 3
                && SnapshotValue(reset.Snapshot) == 3,
                ref failures);
            Check("repeating the daily reset is a database no-op",
                new DailyChallengeService(connectionString)
                    .ResetCharacter(CharacterId)
                    .ChangedEntries == 0,
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: true),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("client increment cannot exceed the persisted daily target",
                ReadChallengeValue(connectionString) == 3
                && rebuiltSender.LastAckBody == null
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && IsExpectedSnapshot(rebuiltSender.LastNotiBody, remaining: 3),
                ref failures);

            SaveNormalActiveQuest(connectionString);
            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(NormalQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("server-owned hunt quest only echoes a client trigger",
                ReadNormalQuestValue(connectionString) == 1
                && ReadChallengeValue(connectionString) == 3,
                ref failures);
            Check("normal quest does not emit a daily challenge snapshot",
                rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "ACK:0021",
                ref failures);

            Check("PVF group index 4 resolves the configured level-86 reward",
                DailyChallengeData.TryResolveReward(4, 86, 2, out var configuredReward)
                && configuredReward.RequiredCompletionCount == 2
                && configuredReward.ItemId == 10099412
                && configuredReward.ItemCount == 1,
                ref failures);

            InventoryContext.Unregister(triggerSessionId, CharacterId);
            SeedCompletedRewardGroup(connectionString);
            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            InventoryContext.Register(sessionId, inventory);
            try
            {
                var rewardManager = new QuestManager(rebuiltSender, connectionString);
                var firstClaim = rewardManager.HandleDailyChallengeReward(
                    sessionId,
                    BitConverter.GetBytes(4));
                Check("first reward claim grants one configured item",
                    firstClaim.Status == DailyChallengeRewardClaimStatus.Success
                    && inventory.CountMainItem(configuredReward.ItemId) == configuredReward.ItemCount,
                    ref failures);
                Check("reward claim persists group flag 4",
                    ReadClaimed(connectionString, 4)
                    && firstClaim.Snapshot.DailyChallengeRewardClaimFlags[4] == 1,
                    ref failures);
                Check("reward success ACK matches A14 handler layout",
                    BitConverter.ToString(DailyChallengeRewardAckBuilder.Build(firstClaim))
                        == "01-04-00-00-00-00-00-00-00",
                    ref failures);
                Check("0x0286 projects persisted claimed flag 4",
                    ReadClaimFlags(DailyChallengeBodyBuilder.Build(firstClaim.Snapshot))[4] == 1,
                    ref failures);

                var replay = rewardManager.HandleDailyChallengeReward(
                    sessionId,
                    BitConverter.GetBytes(4));
                Check("replayed reward claim is idempotent success",
                    replay.Status == DailyChallengeRewardClaimStatus.AlreadyClaimed
                    && replay.ClientSuccess
                    && inventory.CountMainItem(configuredReward.ItemId) == configuredReward.ItemCount,
                    ref failures);

                var rebuiltRewardManager = new QuestManager(rebuiltSender, connectionString);
                var relogReplay = rebuiltRewardManager.HandleDailyChallengeReward(
                    sessionId,
                    BitConverter.GetBytes(4));
                Check("rebuilt service retains claimed state",
                    relogReplay.Status == DailyChallengeRewardClaimStatus.AlreadyClaimed
                    && relogReplay.Snapshot.DailyChallengeRewardClaimFlags[4] == 1,
                    ref failures);

                var rewardReset = new DailyChallengeService(connectionString)
                    .ResetCharacter(CharacterId);
                Check("daily reset clears reward claims",
                    rewardReset.ClearedClaims == 1
                    && !ReadClaimed(connectionString, 4)
                    && rewardReset.Snapshot.DailyChallengeRewardClaimFlags[4] == 0,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }

            CompleteRewardGroup(connectionString);
            var fullSessionId = Guid.NewGuid();
            var fullInventory = BuildFullInventory();
            InventoryContext.Register(fullSessionId, fullInventory);
            try
            {
                var fullClaim = new QuestManager(rebuiltSender, connectionString)
                    .HandleDailyChallengeReward(fullSessionId, BitConverter.GetBytes(4));
                Check("full inventory rejects reward without claiming it",
                    fullClaim.Status == DailyChallengeRewardClaimStatus.InventoryFull
                    && !ReadClaimed(connectionString, 4)
                    && fullClaim.Snapshot.DailyChallengeRewardClaimFlags[4] == 0,
                    ref failures);
                Check("reward failure ACK uses the minimal A14 failure layout",
                    BitConverter.ToString(DailyChallengeRewardAckBuilder.Build(fullClaim)) == "00-00",
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(fullSessionId, CharacterId);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildWireSetTriggerBody(ushort questId, bool increment)
        {
            var body = new byte[6];
            BitConverter.GetBytes((ushort)0x0021).CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            body[4] = 0;
            body[5] = increment ? (byte)1 : (byte)0;
            return body;
        }

        private static bool IsExpectedSnapshot(byte[] body, uint remaining)
        {
            if (body == null || body.Length != 46)
                return false;

            return BitConverter.ToUInt32(body, 0) == 86
                && BitConverter.ToUInt32(body, 4) == 1
                && BitConverter.ToUInt32(body, 8) == 5
                && BitConverter.ToUInt32(body, 12) == 1
                && BitConverter.ToUInt32(body, 16) == ChallengeQuestId
                && BitConverter.ToUInt32(body, 20) == remaining
                && BitConverter.ToUInt32(body, 24) == 3
                && BitConverter.ToUInt32(body, 28) == 6
                && BitConverter.ToUInt32(body, 38) == 1
                && BitConverter.ToUInt32(body, 42) == 777;
        }

        private static uint SnapshotValue(Game.SelectCharacter.SelectCharacterInitializationSnapshot snapshot)
        {
            if (snapshot?.RacingDungeonGroups.Count != 1
                || snapshot.RacingDungeonGroups[0].Entries.Count != 1)
            {
                return uint.MaxValue;
            }

            return snapshot.RacingDungeonGroups[0].Entries[0].ValueB;
        }

        private static byte[] ReadClaimFlags(byte[] body)
        {
            var offset = 4;
            var groupCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4;
            for (var group = 0; group < groupCount; group++)
            {
                offset += 4;
                var entryCount = checked((int)BitConverter.ToUInt32(body, offset));
                offset += 4 + entryCount * 12;
            }

            var flagCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4;
            var flags = new byte[flagCount];
            Buffer.BlockCopy(body, offset, flags, 0, flagCount);
            return flags;
        }

        private static InventoryService BuildFullInventory()
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            if (!InventoryRewardGrantService.TryCreateOnly(
                    10099407,
                    ItemCreateReason.AdminGrant,
                    1,
                    out var created)
                || created?.Core == null)
            {
                throw new InvalidOperationException("daily challenge full-inventory fixture item failed");
            }

            for (short slot = InventoryService.MainSlotStart;
                slot <= InventoryService.MainSlotEnd;
                slot++)
            {
                inventory.AttachItem(InventoryListType.Main, slot, created.Core.Copy());
            }

            inventory.ClearDirtyState();
            return inventory;
        }

        private static void SeedCompletedRewardGroup(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO character_daily_challenge_groups (character_id, group_index, group_id)
VALUES (@cid, 4, 4);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 4, 0, 14734, 1, 0),
       (@cid, 4, 1, 14738, 1, 0);";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void CompleteRewardGroup(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_daily_challenge_entries
SET value_b = 0
WHERE character_id = @cid AND group_index = 4;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool ReadClaimed(string connectionString, int groupIndex)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT 1
FROM character_daily_challenge_claims
WHERE character_id = @cid AND group_index = @groupIndex;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@groupIndex", groupIndex);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'daily-challenge-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@cid, @aid, 'daily-challenge-selftest', 86);
INSERT INTO character_init_flags (character_id, racing_dungeon_current_enter_count)
VALUES (@cid, 7);
INSERT INTO character_daily_challenge_groups (character_id, group_index, group_id)
VALUES (@cid, 0, 5);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 0, 0, @questId, 3, 3);
INSERT INTO character_daily_challenge_tail_ids (character_id, sort_order, id_value)
VALUES (@cid, 0, 777);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@questId", ChallengeQuestId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SaveNormalActiveQuest(string connectionString)
        {
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = NormalQuestId,
                        TriggerValue = 1,
                    },
                });
        }

        private static uint ReadChallengeValue(string connectionString)
        {
            return ReadUInt32(
                connectionString,
                "SELECT value_b FROM character_daily_challenge_entries "
                + "WHERE character_id=@cid AND track_like_id=@id;",
                ChallengeQuestId);
        }

        private static uint ReadNormalQuestValue(string connectionString)
        {
            return ReadUInt32(
                connectionString,
                "SELECT trigger_value FROM character_active_quests "
                + "WHERE character_id=@cid AND quest_id=@id;",
                NormalQuestId);
        }

        private static uint ReadUInt32(
            string connectionString,
            string sql,
            ushort id)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@id", id);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? uint.MaxValue
                        : (uint)Convert.ToInt64(value);
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal List<string> Calls { get; } = new List<string>();
            internal byte[] LastAckBody { get; private set; }
            internal byte[] LastNotiBody { get; private set; }

            public PlayerContext Player { get; } = new PlayerContext
            {
                CharacterId = DailyChallengeSelfTest.CharacterId,
                Level = 86,
            };

            public int CharacterId => DailyChallengeSelfTest.CharacterId;
            public int AccountId => DailyChallengeSelfTest.AccountId;

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                Calls.Add($"NOTI:{notiType:X4}");
                LastNotiBody = body;
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                Calls.Add($"ACK:{cmdType:X4}");
                LastAckBody = body;
                return Task.CompletedTask;
            }

            internal void Reset()
            {
                Calls.Clear();
                LastAckBody = null;
                LastNotiBody = null;
            }
        }
    }
}
