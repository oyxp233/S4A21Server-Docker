using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class SlotExpansionQuestSelfTest
    {
        private const int CharacterId = 169001;
        private const int AccountId = 169001;
        private const ushort SupportSlotQuestId = 649;
        private const ushort MagicStoneQuestId = 650;

        public static int Run()
        {
            Console.WriteLine("=== SLOT_EXPANSION_QUEST selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "slot-expansion-quest.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("slot-expansion-test"),
                Job = 0,
                GrowType = 0,
                Level = 65,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);
            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            InventoryContext.Register(sessionId, inventory);

            var failures = 0;

            var supportReward = GameWorld.QuestData.ResolveReward(SupportSlotQuestId);
            Check("support slot reward definition resolves explicitly",
                supportReward.IsValid
                    && supportReward.Reward.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion
                    && supportReward.Reward.GrowNumber == 21,
                ref failures);
            if (!supportReward.IsValid)
                Console.WriteLine($"[DIAG] support reward error: {supportReward.Error}");

            Check("slot-expansion fixture contains both quests' seeking requirements",
                SeedSeekingRequirements(
                    inventory,
                    SupportSlotQuestId,
                    MagicStoneQuestId),
                ref failures);
            QuestService.SaveActiveQuests(
                connStr,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = SupportSlotQuestId,
                        TriggerValue = 0,
                    },
                });

            var supportAck = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                BuildFinishBody(SupportSlotQuestId));
            if (!IsSuccessAck(supportAck))
                Console.WriteLine($"[DIAG] support finish error=0x{supportAck?.ErrorCode ?? 0xFF:X2}");
            Check("support quest success", IsSuccessAck(supportAck), ref failures);
            Check("support quest chainType is slot expansion",
                TryReadChain(supportAck, out var supportChainType, out var supportSlotId)
                    && supportChainType == GameWorld.QuestData.ChainTypeSlotExpansion
                    && supportSlotId == 21,
                ref failures);
            Check("support slot flag persisted", LoadExEquipSlotStat(dbPath) == 0x01, ref failures);

            QuestService.SaveActiveQuests(
                connStr,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = MagicStoneQuestId,
                        TriggerValue = 0,
                    },
                });

            var magicAck = QuestSelfTestCommandAdapter.HandleFinish(
                questService,
                CharacterId,
                BuildFinishBody(MagicStoneQuestId));
            if (!IsSuccessAck(magicAck))
                Console.WriteLine($"[DIAG] magic finish error=0x{magicAck?.ErrorCode ?? 0xFF:X2}");
            Check("magic stone quest success", IsSuccessAck(magicAck), ref failures);
            Check("magic stone quest chainType is slot expansion",
                TryReadChain(magicAck, out var magicChainType, out var magicSlotId)
                    && magicChainType == GameWorld.QuestData.ChainTypeSlotExpansion
                    && magicSlotId == 22,
                ref failures);
            Check("support + magic stone flags persisted", LoadExEquipSlotStat(dbPath) == 0x03, ref failures);

            InventoryContext.Unregister(sessionId, CharacterId);
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool SeedSeekingRequirements(
            InventoryService inventory,
            params ushort[] questIds)
        {
            var totals = new Dictionary<int, int>();
            foreach (var questId in questIds)
            {
                if (!GameWorld.QuestData.TryResolveCompletionDefinition(
                        questId,
                        out var definition,
                        out _))
                {
                    return false;
                }

                foreach (var item in definition.SeekingItems)
                {
                    if (item.ItemId < 0 || item.Count <= 0)
                        return false;
                    totals.TryGetValue(item.ItemId, out var current);
                    totals[item.ItemId] = checked(current + item.Count);
                }
            }

            foreach (var requirement in totals)
            {
                if (InventoryService.TryResolveMainVirtualSlotByItemId(
                        requirement.Key,
                        out var virtualSlot,
                        out var virtualItemId))
                {
                    if (!inventory.SetMainVirtualCount(
                            virtualSlot,
                            virtualItemId,
                            requirement.Value))
                    {
                        return false;
                    }
                    continue;
                }

                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory,
                        requirement.Key,
                        ItemCreateReason.QuestReward,
                        requirement.Value,
                        out _))
                {
                    return false;
                }
            }

            return totals.Count > 0;
        }

        private static byte[] BuildFinishBody(ushort questId) =>
            QuestSelfTestCommandAdapter.BuildFinishBody(questId);

        private static bool IsSuccessAck(QuestFinishResult result)
        {
            return result != null && result.Success;
        }

        private static bool TryReadChain(QuestFinishResult result, out int chainType, out int rewardValue)
        {
            chainType = result != null ? result.ChainType : -1;
            rewardValue = result != null ? result.GrowNumber : -1;
            return result != null && result.Success;
        }

        private static void SeedAccount(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "slot-expansion-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static int LoadExEquipSlotStat(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT ex_equip_slot_stat FROM characters WHERE character_id=@cid;";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
