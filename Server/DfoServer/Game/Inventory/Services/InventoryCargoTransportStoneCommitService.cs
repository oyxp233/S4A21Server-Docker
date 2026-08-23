using DfoServer.Game.Characters;
using DfoServer.Game.Mailbox;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCargoTransportStoneCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            CargoTransportStoneRequest request,
            CharacterRecord targetCharacter,
            MailboxService mailboxService,
            out CargoTransportStoneResult result,
            out bool persistenceFailed)
        {
            result = new CargoTransportStoneResult
            {
                Request = request ?? new CargoTransportStoneRequest(),
            };
            persistenceFailed = false;

            CargoTransportStoneResult committedResult = result;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "cargo-transport-stone",
                (connection, transaction) =>
                    InventoryCargoTransportStoneService.TryUse(
                        connection,
                        transaction,
                        lease.Inventory,
                        request,
                        targetCharacter,
                        mailboxService,
                        out committedResult));

            result = committedResult ?? result;
            if (!committed)
            {
                persistenceFailed = true;
                return false;
            }

            return result.Success;
        }
    }
}
