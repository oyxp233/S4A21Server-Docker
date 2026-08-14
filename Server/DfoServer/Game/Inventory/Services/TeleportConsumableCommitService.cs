using DfoServer.Game.Characters;

namespace DfoServer.Game.Inventory
{
    internal static class TeleportConsumableCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            int itemTemplateId,
            byte townId,
            byte areaId,
            short posX,
            short posY,
            byte direction,
            byte areaState,
            bool persistPosition,
            out InventoryMainItemConsumeResult consumeResult)
        {
            consumeResult = null;
            InventoryMainItemConsumeResult committedConsume = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "teleport-consumable",
                (connection, transaction) =>
                {
                    if (!lease.Inventory.TryConsumeMainItem(
                            itemTemplateId,
                            1,
                            out committedConsume)
                        || committedConsume == null
                        || !committedConsume.Success)
                    {
                        return false;
                    }

                    return !persistPosition
                        || SqliteCharacterRepository.UpdatePositionInTransaction(
                            connection,
                            transaction,
                            lease.CharacterId,
                            townId,
                            areaId,
                            posX,
                            posY,
                            direction,
                            areaState);
                });
            consumeResult = committedConsume;
            return committed;
        }
    }
}
