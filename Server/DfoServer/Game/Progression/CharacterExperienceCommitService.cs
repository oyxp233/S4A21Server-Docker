using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using System;

namespace DfoServer.Game.Progression
{
    internal static class CharacterExperienceCommitService
    {
        internal static bool TryCommitTournamentExperience(
            InventoryLease lease,
            PlayerContext player,
            int accountId,
            uint rawGain,
            CharacterExperienceService experienceService,
            out ExperienceGrantResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null
                || player == null
                || player.CharacterId <= 0
                || experienceService == null
                || rawGain == 0)
                return false;

            var applied = false;
            var applyCompleted = false;
            var persistenceRejected = false;
            ExperienceGrantResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "tournament-experience",
                (connection, transaction) =>
                {
                    committedResult = CharacterExperienceService.GrantInTransaction(
                        connection,
                        transaction,
                        player.CharacterId,
                        accountId,
                        player.Level,
                        player.Exp,
                        rawGain,
                        normalizeMaxLevelExp: true);
                    persistenceRejected = committedResult != null
                        && RequiresCharacterPersistence(committedResult)
                        && !committedResult.Persisted;
                    applied = committedResult != null
                        && !persistenceRejected;
                    applyCompleted = true;
                    return applied;
                });

            result = committedResult;
            persistenceFailed = !committed
                && (!applyCompleted || applied || persistenceRejected);
            if (!applied || !committed || committedResult == null)
                return false;

            player.Level = committedResult.NewLevel;
            player.Exp = committedResult.NewExp;
            try
            {
                experienceService.PopulateAccountProgressSummary(
                    committedResult,
                    accountId);
            }
            catch (Exception exception)
            {
                FileLogger.Log(
                    $"[Progression] tournament account summary projection failed "
                    + $"account={accountId} cid={player.CharacterId}: {exception.Message}");
            }

            return true;
        }

        private static bool RequiresCharacterPersistence(
            ExperienceGrantResult result)
        {
            return result != null
                && (result.LeveledUp
                    || result.NormalExpGain > 0
                    || result.NormalizedMaxLevelExp);
        }
    }
}
