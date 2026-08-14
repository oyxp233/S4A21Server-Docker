using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class ExpertContractSkillSelfTest
    {
        private const int CharacterId = 999005;
        private const int OtherCharacterId = 999007;
        private const int AccountId = 1;
        private const int ExpertContractPremiumType = 27;
        private const byte CharacterLevel = 86;
        private static readonly string DatabasePath =
            Path.Combine(Path.GetTempPath(), "expert_contract_skill_selftest.db");

        private static int _passed;
        private static int _failed;

        public static int Run()
        {
            Console.WriteLine("=== Expert contract skill self-test ===");
            _passed = 0;
            _failed = 0;
            DeleteSqliteFiles(DatabasePath);

            try
            {
                SkillStaticData skill = null;
                try
                {
                    skill = SkillDataProvider.GetSkill(0, 64);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  PVF read failed: " + ex.Message);
                }

                Check("SkillDataProvider finds skill 64", skill != null);
                if (skill != null)
                    CheckExpiredExpertContractReconcilesSkills(skill);
            }
            finally
            {
                DeleteSqliteFiles(DatabasePath);
            }

            Console.WriteLine($"Expert contract skill self-test: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void CheckExpiredExpertContractReconcilesSkills(SkillStaticData skill)
        {
            var contractEffects = DfoServer.Game.Premium.PremiumCatalog.Load()
                .GetEffects(ExpertContractPremiumType);
            Check(
                "PVF expert contract grants over-skill levels",
                contractEffects != null && contractEffects.OverSkillLevel > 0);
            if (contractEffects == null || contractEffects.OverSkillLevel <= 0)
                return;

            var ordinaryMax = skill.GetMaxLearnableLevel(CharacterLevel, 0, 0);
            var contractMax = skill.GetMaxLearnableLevel(
                CharacterLevel + contractEffects.OverSkillLevel,
                0,
                0);
            Check($"expert contract raises skill64 cap above {ordinaryMax}", contractMax > ordinaryMax);

            var otherCharacterLevel = (byte)Math.Max(1, skill.RequiredLevel - 1);
            var otherOrdinaryMax = skill.GetMaxLearnableLevel(otherCharacterLevel, 0, 0);
            var otherContractMax = skill.GetMaxLearnableLevel(
                otherCharacterLevel + contractEffects.OverSkillLevel,
                0,
                0);
            Check(
                "expert contract exposes a normally unavailable skill",
                otherOrdinaryMax == 0 && otherContractMax > 0);
            if (contractMax <= ordinaryMax || otherOrdinaryMax != 0 || otherContractMax <= 0)
                return;

            var purchasedLevel = ordinaryMax + 1;
            var repo = new SqliteCharacterProgressRepository(
                DatabasePath,
                ServerPaths.SchemaFilePath);
            var characterRepository = new DfoServer.Game.Characters.SqliteCharacterRepository(
                DatabasePath,
                ServerPaths.SchemaFilePath);
            EnsureTestCharacter(DatabasePath, CharacterId, CharacterLevel);
            EnsureTestCharacter(DatabasePath, OtherCharacterId, otherCharacterLevel);
            SetPremiumEndTime(
                DatabasePath,
                AccountId,
                ExpertContractPremiumType,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);

            var seed = new SkillInfoSnapshot();
            seed.Pages.Add(new SkillInfoPageSnapshot());
            seed.Pages.Add(new SkillInfoPageSnapshot());
            SeedSkillProgress(repo, CharacterId, seed, CharacterLevel);
            SeedSkillProgress(repo, OtherCharacterId, seed, otherCharacterLevel);

            var request = new List<BuySkillEntry>
            {
                new BuySkillEntry
                {
                    SkillIndex = (ushort)skill.SkillIndex,
                    Level = (byte)purchasedLevel,
                    IsRefund = 0,
                }
            };
            var page0Purchase = BuySkillService.Execute(
                repo,
                CharacterId,
                AccountId,
                0,
                0,
                request,
                level: CharacterLevel);
            var page1Purchase = BuySkillService.Execute(
                repo,
                CharacterId,
                AccountId,
                0,
                1,
                request,
                level: CharacterLevel);
            var otherCharacterPurchase = BuySkillService.Execute(
                repo,
                OtherCharacterId,
                AccountId,
                0,
                0,
                new List<BuySkillEntry>
                {
                    new BuySkillEntry
                    {
                        SkillIndex = (ushort)skill.SkillIndex,
                        Level = 1,
                        IsRefund = 0,
                    }
                },
                level: otherCharacterLevel);
            Check(
                "active expert contract permits page0 over-level skill",
                page0Purchase != null
                && page0Purchase.Success
                && page0Purchase.Entries.Count == 1
                && page0Purchase.Entries[0].Level == purchasedLevel);
            Check(
                "active expert contract permits page1 over-level skill",
                page1Purchase != null
                && page1Purchase.Success
                && page1Purchase.Entries.Count == 1
                && page1Purchase.Entries[0].Level == purchasedLevel);
            Check(
                "active expert contract permits another character unavailable skill",
                otherCharacterPurchase != null
                && otherCharacterPurchase.Success
                && otherCharacterPurchase.Entries.Count == 1
                && otherCharacterPurchase.Entries[0].Level == 1);

            var beforeExpiry = repo.LoadSkills(CharacterId);
            var beforePage0 = SkillPointLedger.Compute(
                0,
                CharacterLevel,
                0,
                0,
                beforeExpiry,
                0);
            var beforePage1 = SkillPointLedger.Compute(
                0,
                CharacterLevel,
                0,
                0,
                beforeExpiry,
                1);
            var expectedRefund = skill.SpCostFor(ordinaryMax, purchasedLevel);

            var selectData = new SqliteSelectCharacterDataSource(
                DatabasePath,
                ServerPaths.SchemaFilePath,
                characterRepository);
            selectData.PrepareForSkillSynchronization(CharacterId, AccountId);
            var activeSelected = selectData.Load(CharacterId, AccountId);
            var activeSelectedSkills = activeSelected.InitializationSnapshot.SkillInfo;
            Check(
                "active expert contract keeps page0 over-level skill",
                activeSelectedSkills.Pages[0].Entries.Find(
                    entry => entry.SkillId == skill.SkillIndex)?.Level == purchasedLevel);
            Check(
                "active expert contract keeps page1 over-level skill",
                activeSelectedSkills.Pages[1].Entries.Find(
                    entry => entry.SkillId == skill.SkillIndex)?.Level == purchasedLevel);

            SetPremiumEndTime(
                DatabasePath,
                AccountId,
                ExpertContractPremiumType,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);

            var passiveRefresh = selectData.Load(CharacterId, AccountId);
            Check(
                "expired expert contract waits for explicit skill synchronization before reconciling",
                passiveRefresh.InitializationSnapshot.SkillInfo.Pages[0].Entries.Find(
                    entry => entry.SkillId == skill.SkillIndex)?.Level == purchasedLevel);
            Check(
                "ordinary snapshot refresh leaves the expired premium state row untouched",
                PremiumRowExists(DatabasePath, AccountId, ExpertContractPremiumType));

            selectData.PrepareForSkillSynchronization(CharacterId, AccountId);
            var selected = selectData.Load(CharacterId, AccountId);
            var selectedSkills = selected.InitializationSnapshot.SkillInfo;
            var selectedPage0 = selectedSkills.Pages[0].Entries.Find(
                entry => entry.SkillId == skill.SkillIndex);
            var selectedPage1 = selectedSkills.Pages[1].Entries.Find(
                entry => entry.SkillId == skill.SkillIndex);
            Check(
                $"expired expert contract caps page0 skill64 at {ordinaryMax}",
                selectedPage0 != null && selectedPage0.Level == ordinaryMax);
            Check(
                $"expired expert contract caps page1 skill64 at {ordinaryMax}",
                selectedPage1 != null && selectedPage1.Level == ordinaryMax);
            Check(
                $"expired expert contract refunds page0 SP={expectedRefund}",
                selectedSkills.Pages[0].HeaderValue == beforePage0.RemainingSp + expectedRefund);
            Check(
                $"expired expert contract refunds page1 SP={expectedRefund}",
                selectedSkills.Pages[1].HeaderValue == beforePage1.RemainingSp + expectedRefund);

            var persisted = repo.LoadSkills(CharacterId);
            var persistedPage0 = persisted.Pages[0].Entries.Find(
                entry => entry.SkillId == skill.SkillIndex);
            var persistedPage1 = persisted.Pages[1].Entries.Find(
                entry => entry.SkillId == skill.SkillIndex);
            Check(
                "expired expert contract persists page0 capped level",
                persistedPage0 != null && persistedPage0.Level == ordinaryMax);
            Check(
                "expired expert contract persists page1 capped level",
                persistedPage1 != null && persistedPage1.Level == ordinaryMax);

            var otherBeforeOwnSynchronization = repo.LoadSkills(OtherCharacterId);
            Check(
                "expired expert contract leaves another character unchanged until its own skill synchronization",
                otherBeforeOwnSynchronization.Pages[0].Entries.Find(
                    entry => entry.SkillId == skill.SkillIndex)?.Level == 1);
            Check(
                "expired expert contract keeps the persisted premium state row",
                PremiumRowExists(DatabasePath, AccountId, ExpertContractPremiumType));

            selectData.PrepareForSkillSynchronization(OtherCharacterId, AccountId);
            var otherSelected = selectData.Load(OtherCharacterId, AccountId);
            var otherSelectedSkills = otherSelected.InitializationSnapshot.SkillInfo;
            Check(
                "expired expert contract removes the unavailable skill when that character synchronizes",
                otherSelectedSkills.Pages[0].Entries.Find(
                    entry => entry.SkillId == skill.SkillIndex) == null);
            var otherPoints = SkillPointLedger.Compute(
                0,
                otherCharacterLevel,
                0,
                0,
                otherSelectedSkills,
                0);
            Check(
                "expired expert contract refunds all unavailable-skill SP on that character",
                otherPoints.RemainingSp == otherPoints.TotalSp);
            Check(
                "per-character reconciliation does not consume the persisted premium state row",
                PremiumRowExists(DatabasePath, AccountId, ExpertContractPremiumType));

            selectData.PrepareForSkillSynchronization(CharacterId, AccountId);
            var selectedAgain = selectData.Load(CharacterId, AccountId);
            Check(
                "repeated synchronization keeps the already capped skill unchanged",
                selectedAgain.InitializationSnapshot.SkillInfo.Pages[0].Entries.Find(
                    entry => entry.SkillId == skill.SkillIndex)?.Level == ordinaryMax);
        }

        private static void SetPremiumEndTime(
            string databasePath,
            int accountId,
            int premiumType,
            long endTime)
        {
            using (var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO account_premiums (account_id, premium_type, end_time)
VALUES (@accountId, @premiumType, @endTime)
ON CONFLICT(account_id, premium_type)
DO UPDATE SET end_time=@endTime;";
                    command.Parameters.AddWithValue("@accountId", accountId);
                    command.Parameters.AddWithValue("@premiumType", premiumType);
                    command.Parameters.AddWithValue("@endTime", endTime);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool PremiumRowExists(
            string databasePath,
            int accountId,
            int premiumType)
        {
            using (var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT EXISTS (
    SELECT 1
    FROM account_premiums
    WHERE account_id=@accountId
      AND premium_type=@premiumType
);";
                    command.Parameters.AddWithValue("@accountId", accountId);
                    command.Parameters.AddWithValue("@premiumType", premiumType);
                    return Convert.ToInt32(command.ExecuteScalar()) != 0;
                }
            }
        }

        private static void SeedSkillProgress(
            SqliteCharacterProgressRepository repository,
            int characterId,
            SkillInfoSnapshot skills,
            byte level)
        {
            var points = SkillStateService.ResolvePointState(skills, 0, level, 0, 0);
            SkillStateService.Persist(repository, characterId, skills, points);
        }

        private static void EnsureTestCharacter(
            string databasePath,
            int characterId,
            byte level)
        {
            using (var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (1, 'selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, 1, @name);";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    command.Parameters.AddWithValue("@name", $"selftest-{characterId}");
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE characters
SET job = 0, level = @level, bonus_sp = 0, bonus_tp = 0
WHERE character_id = @characterId;";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    command.Parameters.AddWithValue("@level", (int)level);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteSqliteFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    if (File.Exists(databasePath + suffix))
                        File.Delete(databasePath + suffix);
                }
                catch
                {
                    // Best-effort cleanup for an isolated self-test database.
                }
            }
        }

        private static void Check(string label, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}");
            if (passed)
                _passed++;
            else
                _failed++;
        }
    }
}
