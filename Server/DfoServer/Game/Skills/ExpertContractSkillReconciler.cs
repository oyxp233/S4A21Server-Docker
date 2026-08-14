using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    internal static class ExpertContractSkillReconciler
    {
        internal static bool ReconcileExpiredContractSkills(
            SqliteCharacterProgressRepository repository,
            int characterId,
            int accountId)
        {
            if (repository == null || characterId <= 0 || accountId <= 0)
                return false;

            var premiumCatalog = Game.Premium.PremiumCatalog.Load();
            CharacterSkillProfile.Warmup();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using (var connection = new SqliteConnection(repository.ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!HasExpiredExpertContract(
                            connection,
                            transaction,
                            accountId,
                            now,
                            premiumCatalog))
                    {
                        transaction.Commit();
                        return false;
                    }

                    var character = repository.LoadProgressSnapshot(
                        connection,
                        transaction,
                        characterId);
                    if (character == null || character.AccountId != accountId)
                    {
                        transaction.Commit();
                        return false;
                    }

                    Characters.CharacterStatComputer.DecodeGrowType(
                        character.GrowType,
                        out var growType,
                        out var secondGrowType);
                    var skills = repository.LoadSkills(
                        connection,
                        transaction,
                        characterId);
                    var baseline = SkillPointLedger.BuildFreeBaseline(
                        character.Job,
                        growType,
                        secondGrowType);
                    var changedEntries = ReconcileCharacterSkills(
                        skills,
                        character.Job,
                        character.Level,
                        growType,
                        secondGrowType,
                        baseline);
                    if (changedEntries > 0)
                    {
                        repository.SaveSkillProgress(
                            connection,
                            transaction,
                            characterId,
                            skills);
                    }

                    transaction.Commit();
                    if (changedEntries == 0)
                        return false;

                    FileLogger.Log(
                        $"[ExpertContractSkillReconciler] reconciled aid={accountId} " +
                        $"cid={characterId} entries={changedEntries}");
                    return true;
                }
            }
        }

        private static bool HasExpiredExpertContract(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            long now,
            Game.Premium.PremiumCatalog premiumCatalog)
        {
            // Existing premium helpers only expose active effects and open their own
            // connections. Keep the expired-state check in the skill write transaction.
            var hasExpiredContract = false;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT premium_type, end_time
FROM account_premiums
WHERE account_id=@aid;";
                command.Parameters.AddWithValue("@aid", accountId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var effects = premiumCatalog.GetEffects(reader.GetInt32(0));
                        if (effects == null || effects.OverSkillLevel <= 0)
                            continue;

                        if (reader.GetInt64(1) > now)
                            return false;

                        hasExpiredContract = true;
                    }
                }
            }

            return hasExpiredContract;
        }

        private static int ReconcileCharacterSkills(
            SkillInfoSnapshot skills,
            byte job,
            byte level,
            int growType,
            int secondGrowType,
            IReadOnlyDictionary<ushort, byte> baseline)
        {
            var changedEntries = 0;
            foreach (var page in skills.Pages)
            {
                for (var entryIndex = page.Entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    var entry = page.Entries[entryIndex];
                    var skill = SkillDataProvider.GetSkill(job, entry.SkillId);
                    if (skill == null || skill.IsFixedLevelSkill)
                        continue;

                    // Skills unavailable to this advancement were not learned through
                    // the expert contract, so preserve quest/task/free grants.
                    if (skill.GetMaxLevelFor(growType, secondGrowType) <= 0)
                        continue;

                    var maximum = skill.GetMaxLearnableLevel(level, growType, secondGrowType);
                    if (baseline.TryGetValue(entry.SkillId, out var freeLevel)
                        && freeLevel > maximum)
                    {
                        maximum = freeLevel;
                    }

                    if (entry.Level <= maximum)
                        continue;

                    if (maximum <= 0)
                        page.Entries.RemoveAt(entryIndex);
                    else
                        entry.Level = (byte)Math.Min(maximum, byte.MaxValue);
                    changedEntries++;
                }
            }

            return changedEntries;
        }
    }
}
