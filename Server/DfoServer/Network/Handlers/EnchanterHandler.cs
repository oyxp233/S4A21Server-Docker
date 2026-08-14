using System;
using System.Threading.Tasks;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;

namespace DfoServer.Network.Handlers
{
    internal sealed class EnchanterHandler
    {
        private const ushort RepairCommand = (ushort)CmdPacketType.REPAIR_EXPERT_JOB_STORE;

        private readonly ExpertJobStoreRuntimeService _stores;
        private readonly IEnchanterMachineStateRepository _machines;
        private readonly ExpertJobPersistenceService _persistence;
        private readonly ExpertJobOperationCoordinator _operations;

        internal EnchanterHandler(
            ExpertJobStoreRuntimeService stores,
            IEnchanterMachineStateRepository machines,
            ExpertJobPersistenceService persistence,
            ExpertJobOperationCoordinator operations)
        {
            _stores = stores ?? throw new ArgumentNullException(nameof(stores));
            _machines = machines ?? throw new ArgumentNullException(nameof(machines));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        internal async Task HandleRepair(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!RepairExpertJobStoreRequest.IsValid(body)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != ExpertJobStateCodec.EnchanterType
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendRepairError(session, EnchanterMachineRepairService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            try
            {
                if (_stores.HasStore(player.CharacterId))
                {
                    await SendRepairError(session, EnchanterMachineRepairService.ErrorInvalidState);
                    return;
                }

                var machine = _machines.ResolveEnchanter(player.CharacterId);
                ExpertJobMachineRepairResult result;
                bool success;
                lock (lease.SyncRoot)
                {
                    success = EnchanterMachineRepairService.TryRepair(
                        lease.Inventory,
                        machine,
                        player.Subtype0Tail.ExpertJobExp,
                        out result);
                }
                if (!success)
                {
                    await SendRepairError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _machines.SaveEnchanterInTransaction(
                            connection,
                            transaction,
                            player.CharacterId,
                            machine,
                            0,
                            null)))
                {
                    await SendRepairError(
                        session,
                        EnchanterMachineRepairService.ErrorInvalidState);
                    return;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    RepairCommand,
                    ExpertJobStorePacketBuilder.BuildRepairNotification(
                        result.Gold,
                        result.Endurance)));
                FileLogger.Log(
                    $"[Enchanter] REPAIR cid={player.CharacterId} cost={result.Cost} " +
                    $"endurance={result.Endurance} gold={result.Gold}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        private static Task SendRepairError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                RepairCommand,
                ExpertJobStorePacketBuilder.BuildRepairError(errorCode)));

    }
}
