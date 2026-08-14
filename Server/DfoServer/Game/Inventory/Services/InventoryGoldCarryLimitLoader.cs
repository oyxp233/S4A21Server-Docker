using System;
using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryGoldCarryLimitLoader
    {
        internal static int Load(int characterId)
        {
            return Load(characterId, database: null);
        }

        internal static int Load(InventoryService inventory)
        {
            return Load(
                inventory?.CharacterId ?? 0,
                inventory?.Database);
        }

        internal static int Load(
            int characterId,
            IGameDatabase database)
        {
            if (characterId <= 0)
                return int.MaxValue;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        var limit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                            connection,
                            transaction,
                            characterId);
                        transaction.Commit();
                        return limit <= 0 ? int.MaxValue : limit;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryGoldCarryLimit] load failed cid={characterId}: {ex.Message}");
                return int.MaxValue;
            }
        }
    }
}
