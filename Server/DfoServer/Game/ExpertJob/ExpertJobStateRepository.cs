using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobGiveupState
    {
        internal byte ExpertJobType { get; set; }

        internal int GiveupCount { get; set; }
    }

    internal interface IExpertJobGiveupStateRepository
    {
        ExpertJobGiveupState LoadGiveupStateInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId);

        bool ResetAfterGiveupInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte nextGiveupCount);
    }

    public interface IExpertJobStateRepository
    {
        ExpertJobState Load(int characterId, int expertJobType);

        bool SaveProgressInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds);

        bool SaveRecipeInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int recipeId);
    }

    public interface IDisjointMachineStateRepository
    {
        DisjointMachineState Resolve(int characterId);

        bool SaveInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DisjointMachineState state,
            int experienceGain);
    }

    public interface IEnchanterMachineStateRepository : IExpertJobStateRepository
    {
        EnchanterMachineState ResolveEnchanter(int characterId);

        bool SaveEnchanterProgressInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds);

        bool SaveEnchanterInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EnchanterMachineState state,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds);
    }

    public sealed class SqliteExpertJobStateRepository
        : IExpertJobStateRepository, IDisjointMachineStateRepository,
          IEnchanterMachineStateRepository, IExpertJobGiveupStateRepository
    {
        private readonly string _connectionString;

        public SqliteExpertJobStateRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteExpertJobStateRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public ExpertJobState Load(int characterId, int expertJobType)
        {
            var state = CreateInitialState(expertJobType);
            if (characterId <= 0 || expertJobType <= 0)
                return state;

            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT giveup_count, disjoint_machine_grade, disjoint_machine_endurance,
       enchanter_endurance
FROM character_expert_job
WHERE character_id=@cid;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                state.GiveUpCount = Math.Max(0, reader.GetInt32(0));
                                if (expertJobType == ExpertJobStateCodec.DisjointerType)
                                {
                                    var grade = reader.GetInt32(1);
                                    var endurance = reader.GetInt32(2);
                                    if (IsValidDisjointMachineState(grade, endurance))
                                    {
                                        state.DisjointMachine.MachineGrade = (byte)grade;
                                        state.DisjointMachine.Endurance = endurance;
                                    }
                                }
                                else if (expertJobType == ExpertJobStateCodec.EnchanterType)
                                {
                                    var endurance = reader.GetInt32(3);
                                    state.EnchanterMachine.Endurance = Math.Max(0, endurance);
                                }
                            }
                        }
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT recipe_id
FROM character_expert_job_recipes
WHERE character_id=@cid
ORDER BY recipe_id;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                                state.LearnedRecipeIds.Add(reader.GetInt32(0));
                        }
                    }
                    if (ExpertJobConfigRegistry.TryGetRecipeConfig(
                            expertJobType,
                            out var recipeConfig))
                        ReconcileAutoLearnRecipes(
                            connection,
                            characterId,
                            state,
                            recipeConfig);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobState] load failed cid={characterId}: {ex.Message}");
            }

            return state;
        }

        public DisjointMachineState Resolve(int characterId)
            => Load(characterId, ExpertJobStateCodec.DisjointerType).DisjointMachine;

        public EnchanterMachineState ResolveEnchanter(int characterId)
            => Load(characterId, ExpertJobStateCodec.EnchanterType).EnchanterMachine;

        private static ExpertJobGiveupState LoadGiveupStateInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0)
                return null;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(f.expert_job_type, 0), COALESCE(e.giveup_count, 0)
FROM characters c
LEFT JOIN character_subtype0_fields f ON f.character_id=c.character_id
LEFT JOIN character_expert_job e ON e.character_id=c.character_id
WHERE c.character_id=@cid AND c.delete_flag=0;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    var type = reader.GetInt32(0);
                    return new ExpertJobGiveupState
                    {
                        ExpertJobType = type > 0 && type <= byte.MaxValue
                            ? (byte)type
                            : (byte)0,
                        GiveupCount = Math.Max(0, reader.GetInt32(1)),
                    };
                }
            }
        }

        ExpertJobGiveupState IExpertJobGiveupStateRepository
            .LoadGiveupStateInTransaction(
                SqliteConnection connection,
                SqliteTransaction transaction,
                int characterId)
            => LoadGiveupStateInTransaction(
                connection,
                transaction,
                characterId);

        public bool SaveEnchanterInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EnchanterMachineState state,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds)
        {
            if (connection == null || transaction == null || characterId <= 0
                || state == null || state.Endurance < 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (character_id, enchanter_endurance, updated_at)
VALUES (@cid, @endurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    enchanter_endurance=excluded.enchanter_endurance,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@endurance", state.Endurance);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }
            return SaveProgressInTransaction(
                connection, transaction, characterId, experienceGain, learnedRecipeIds);
        }

        public bool SaveEnchanterProgressInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds)
            => SaveProgressInTransaction(
                connection, transaction, characterId, experienceGain, learnedRecipeIds);

        bool IExpertJobStateRepository.SaveProgressInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds)
            => SaveProgressInTransaction(
                connection, transaction, characterId, experienceGain, learnedRecipeIds);

        public bool SaveRecipeInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int recipeId)
            => SaveRecipesInTransaction(
                connection,
                transaction,
                characterId,
                new[] { recipeId });

        private bool ResetAfterGiveupInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte nextGiveupCount)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, giveup_count,
    disjoint_machine_grade, disjoint_machine_endurance,
    enchanter_endurance, updated_at)
VALUES (@cid, @giveup, 0, 0, 0, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    giveup_count=excluded.giveup_count,
    disjoint_machine_grade=0,
    disjoint_machine_endurance=0,
    enchanter_endurance=0,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@giveup", (int)nextGiveupCount);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_expert_job_recipes
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }

            return true;
        }

        bool IExpertJobGiveupStateRepository.ResetAfterGiveupInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte nextGiveupCount)
            => ResetAfterGiveupInTransaction(
                connection,
                transaction,
                characterId,
                nextGiveupCount);

        public bool SaveInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DisjointMachineState state,
            int experienceGain)
        {
            if (characterId <= 0
                || state == null
                || !IsValidDisjointMachineState(state.MachineGrade, state.Endurance)
                || connection == null
                || transaction == null)
            {
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, disjoint_machine_grade, disjoint_machine_endurance, updated_at)
VALUES (@cid, @grade, @endurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    disjoint_machine_grade=excluded.disjoint_machine_grade,
    disjoint_machine_endurance=excluded.disjoint_machine_endurance,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@grade", state.MachineGrade);
                command.Parameters.AddWithValue("@endurance", state.Endurance);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }

            return SaveExperienceInTransaction(
                connection,
                transaction,
                characterId,
                experienceGain);
        }

        private static bool SaveProgressInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int experienceGain,
            IReadOnlyCollection<int> learnedRecipeIds)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            if (!SaveExperienceInTransaction(
                    connection,
                    transaction,
                    characterId,
                    experienceGain))
                return false;

            return SaveRecipesInTransaction(
                connection,
                transaction,
                characterId,
                learnedRecipeIds);
        }

        private static bool SaveRecipesInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            IReadOnlyCollection<int> recipeIds)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;
            if (recipeIds == null)
                return true;
            foreach (var recipeId in recipeIds)
            {
                if (recipeId <= 0)
                    return false;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO character_expert_job_recipes (character_id, recipe_id)
VALUES (@cid, @recipe);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@recipe", recipeId);
                    command.ExecuteNonQuery();
                }
            }
            return true;
        }

        internal static void InitializeInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int expertJobType)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            var initialGrade = 0;
            var initialEndurance = 0;
            if (expertJobType == ExpertJobStateCodec.DisjointerType)
            {
                initialGrade = 1;
                initialEndurance = DisjointMachineConfigProvider.InitialEndurance;
            }
            var initialEnchanterEndurance = expertJobType == ExpertJobStateCodec.EnchanterType
                ? EnchanterConfigProvider.Config.InitialEndurance
                : 0;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, giveup_count,
    disjoint_machine_grade, disjoint_machine_endurance,
    enchanter_endurance, updated_at)
VALUES (@cid, 0, @grade, @endurance, @enchanterEndurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    disjoint_machine_grade=excluded.disjoint_machine_grade,
    disjoint_machine_endurance=excluded.disjoint_machine_endurance,
    enchanter_endurance=excluded.enchanter_endurance,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@grade", initialGrade);
                command.Parameters.AddWithValue("@endurance", initialEndurance);
                command.Parameters.AddWithValue("@enchanterEndurance", initialEnchanterEndurance);
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_expert_job_recipes
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }

            if (ExpertJobConfigRegistry.TryGetRecipeConfig(
                    expertJobType,
                    out var recipeConfig))
            {
                foreach (var recipeId in recipeConfig.GetAutoLearnRecipeIds(0))
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO character_expert_job_recipes (character_id, recipe_id)
VALUES (@cid, @recipe);";
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.Parameters.AddWithValue("@recipe", recipeId);
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void ReconcileAutoLearnRecipes(
            SqliteConnection connection,
            int characterId,
            ExpertJobState state,
            ExpertJobRecipeConfig recipeConfig)
        {
            uint experience;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT expert_job_exp
FROM character_subtype0_fields
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                experience = value == null || value == DBNull.Value
                    ? 0
                    : (uint)Math.Min(uint.MaxValue, Convert.ToUInt64(value));
            }

            var expected = recipeConfig.GetAutoLearnRecipeIds(experience);
            var missing = new List<int>();
            foreach (var recipeId in expected)
            {
                if (!state.LearnedRecipeIds.Contains(recipeId))
                    missing.Add(recipeId);
            }
            if (missing.Count == 0)
            {
                state.LearnedRecipeIds.Sort();
                return;
            }

            using (var transaction = connection.BeginTransaction())
            {
                foreach (var recipeId in missing)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT OR IGNORE INTO character_expert_job_recipes (character_id, recipe_id)
VALUES (@cid, @recipe);";
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.Parameters.AddWithValue("@recipe", recipeId);
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
            state.LearnedRecipeIds.AddRange(missing);
            state.LearnedRecipeIds.Sort();
        }

        private static bool SaveExperienceInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int experienceGain)
        {
            var normalizedExperienceGain = Math.Max(0, experienceGain);
            if (normalizedExperienceGain == 0)
                return true;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_subtype0_fields
SET expert_job_exp = MIN(4294967295, expert_job_exp + @exp)
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@exp", normalizedExperienceGain);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static ExpertJobState CreateInitialState(int expertJobType)
        {
            var state = new ExpertJobState();
            if (expertJobType == ExpertJobStateCodec.DisjointerType)
            {
                state.DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = 1,
                    Endurance = DisjointMachineConfigProvider.InitialEndurance,
                };
            }
            else if (expertJobType == ExpertJobStateCodec.EnchanterType)
            {
                state.EnchanterMachine = new EnchanterMachineState
                {
                    Endurance = EnchanterConfigProvider.Config.InitialEndurance,
                };
            }

            return state;
        }

        private static bool IsValidDisjointMachineState(int grade, int endurance)
            => grade > 0
                && grade <= DisjointMachineConfigProvider.Config.RepairRules.Count
                && endurance >= 0;
    }
}
