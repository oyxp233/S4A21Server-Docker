using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Raid
{
    internal readonly struct RaidEntryCostMutation
    {
        internal RaidEntryCostMutation(int characterId, short slotIndex)
        {
            CharacterId = characterId;
            SlotIndex = slotIndex;
        }

        internal int CharacterId { get; }

        internal short SlotIndex { get; }
    }

    internal static class RaidEntryCostCommitService
    {
        internal const int EntryTicketItemId = 10096296;

        internal static bool TryConsume(
            IReadOnlyList<InventoryLease> sourceLeases,
            out IReadOnlyList<RaidEntryCostMutation> mutations)
        {
            mutations = Array.Empty<RaidEntryCostMutation>();
            if (sourceLeases == null || sourceLeases.Count == 0)
                return false;

            var leases = sourceLeases
                .Where(lease => lease?.Inventory != null)
                .OrderBy(lease => lease.CharacterId)
                .ToArray();
            if (leases.Length != sourceLeases.Count
                || leases.Select(lease => lease.CharacterId).Distinct().Count()
                    != leases.Length)
            {
                return false;
            }

            var entered = new List<InventoryLease>(leases.Length);
            var pending = new List<RaidEntryCostMutation>(leases.Length);
            var committed = false;
            string connectionString = null;
            try
            {
                foreach (var lease in leases)
                {
                    Monitor.Enter(lease.SyncRoot);
                    entered.Add(lease);
                    if (!InventoryContext.IsCurrentLease(
                            lease,
                            lease.SessionId,
                            lease.CharacterId))
                    {
                        return false;
                    }
                }

                if (leases.Any(lease =>
                        lease.Inventory.CountMainItem(EntryTicketItemId) < 1))
                {
                    return false;
                }

                connectionString = leases[0].Inventory.Database?.ConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString)
                    || leases.Any(lease => !string.Equals(
                        lease.Inventory.Database?.ConnectionString,
                        connectionString,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                using (var connection = leases[0].Inventory.Database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var lease in leases)
                    {
                        if (!lease.Inventory.TryConsumeMainItem(
                                EntryTicketItemId,
                                1,
                                out var consumed)
                            || !consumed.Success)
                        {
                            throw new InvalidOperationException(
                                $"entry ticket consume failed cid={lease.CharacterId}");
                        }

                        pending.Add(new RaidEntryCostMutation(
                            lease.CharacterId,
                            consumed.SlotIndex));
                    }

                    foreach (var lease in leases)
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease))
                        {
                            throw new InvalidOperationException(
                                $"entry cost persistence failed cid={lease.CharacterId}");
                        }
                    }

                    transaction.Commit();
                    foreach (var lease in leases)
                        lease.Inventory.ClearDirtyState();
                    committed = true;
                }

                mutations = pending;
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[RaidEntryCostCommit] transaction failed: {ex.Message}");
                return false;
            }
            finally
            {
                for (var index = entered.Count - 1; index >= 0; index--)
                    Monitor.Exit(entered[index].SyncRoot);

                if (!committed && !string.IsNullOrWhiteSpace(connectionString))
                {
                    foreach (var lease in leases)
                    {
                        try
                        {
                            InventoryRollbackRecoveryService.ReloadOnlineInventory(
                                connectionString,
                                lease);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log(
                                $"[RaidEntryCostCommit] rollback reload failed "
                                + $"cid={lease.CharacterId}: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
