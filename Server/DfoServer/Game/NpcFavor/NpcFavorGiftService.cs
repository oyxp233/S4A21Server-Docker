using DfoServer.Game.Inventory;

namespace DfoServer.Game.NpcFavor
{
    internal sealed class NpcFavorGiftRequest
    {
        internal int NpcId { get; set; }
        internal short SlotIndex { get; set; }
        internal int ExpectedItemId { get; set; }
        internal int ItemCount { get; set; }
        internal bool ConsumeByTemplateId { get; set; }
        internal int DefaultFavorPoint { get; set; }
        internal int FavorPointDelta { get; set; }
        internal int MaximumFavorPoint { get; set; } = NpcFavorProgressionPolicy.MaximumFavorPoint;
        internal int TrustedFavorPoint { get; set; } = NpcFavorProgressionPolicy.TrustedFavorPoint;
        internal int GameDayId { get; set; }
        internal int NpcDailyGiftLimit { get; set; }
    }

    internal enum NpcFavorGiftError
    {
        None = 0,
        InvalidRequest = 1,
        InvalidInventoryItem = 2,
        NpcDailyLimit = 3,
        CommitFailed = 4,
    }

    internal sealed class NpcFavorGiftResult
    {
        internal bool Success { get; set; }
        internal NpcFavorGiftError Error { get; set; }
        internal InventoryDeleteResult InventoryDeletion { get; set; }
        internal InventoryMutationSet InventoryChanges { get; set; }
        internal NpcFavorState State { get; set; }
        internal int AppliedFavorPointDelta { get; set; }
    }

    /// <summary>
    /// 把在线背包扣除与 NPC 好感度写入绑定到同一个 SQLite 事务。
    /// 协议层只负责把已验证的槽位、数量和 PVF 计算结果传入；本类不猜测包体布局。
    /// </summary>
    internal sealed class NpcFavorGiftService
    {
        private readonly NpcFavorRepository _repository;

        internal NpcFavorGiftService(NpcFavorRepository repository)
        {
            _repository = repository;
        }

        internal NpcFavorGiftResult TryGift(
            InventoryLease lease,
            NpcFavorGiftRequest request)
        {
            if (_repository == null
                || lease?.Inventory == null
                || request == null
                || request.NpcId < 0
                || request.ExpectedItemId <= 0
                || request.ItemCount <= 0
                || request.MaximumFavorPoint <= 0
                || request.TrustedFavorPoint <= 0
                || request.TrustedFavorPoint > request.MaximumFavorPoint
                || request.GameDayId <= 0
                || request.NpcDailyGiftLimit <= 0)
            {
                return Fail(NpcFavorGiftError.InvalidRequest);
            }

            if (!request.ConsumeByTemplateId)
            {
                lock (lease.SyncRoot)
                {
                    if (InventoryService.IsVirtualMainSlot(request.SlotIndex))
                    {
                        if (!InventoryService.TryResolveMainVirtualItemId(
                                request.SlotIndex,
                                out var virtualItemId)
                            || virtualItemId != request.ExpectedItemId
                            || !InventoryDeleteService.CanDeleteForClient(
                                lease.Inventory,
                                InventoryListType.Main,
                                request.SlotIndex,
                                request.ItemCount))
                        {
                            return Fail(NpcFavorGiftError.InvalidInventoryItem);
                        }
                    }
                    else
                    {
                        var item = lease.Inventory.GetItem(
                            InventoryListType.Main,
                            request.SlotIndex);
                        if (item == null
                            || item.ItemId != request.ExpectedItemId
                            || item.Count < request.ItemCount
                            || !InventoryStackRuleService.IsStackable(item)
                            || !InventoryDeleteService.CanDeleteForClient(
                            lease.Inventory,
                            InventoryListType.Main,
                            request.SlotIndex,
                            request.ItemCount))
                        {
                            return Fail(NpcFavorGiftError.InvalidInventoryItem);
                        }
                    }
                }
            }

            InventoryDeleteResult deletion = null;
            InventoryMutationSet inventoryChanges = null;
            NpcFavorGiftApplyResult applied = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "npc-favor-gift",
                (connection, transaction) =>
                {
                    bool consumed;
                    if (request.ConsumeByTemplateId)
                    {
                        consumed = InventoryDeleteService.TryDeleteMainItemsByTemplateIdForClient(
                            lease.Inventory,
                            request.ExpectedItemId,
                            request.ItemCount,
                            out inventoryChanges);
                    }
                    else if (InventoryService.IsVirtualMainSlot(request.SlotIndex))
                    {
                        consumed = InventoryDeleteService.TryDeleteForClient(
                            lease.Inventory,
                            InventoryListType.Main,
                            request.SlotIndex,
                            request.ItemCount,
                            out var mutation);
                        inventoryChanges = new InventoryMutationSet();
                        if (consumed && mutation != null)
                            inventoryChanges.AddSlot(mutation.ListType, mutation.SlotIndex);
                    }
                    else
                    {
                        consumed = InventoryDeleteService.TryConsumeFromSlot(
                            lease.Inventory,
                            InventoryListType.Main,
                            request.SlotIndex,
                            request.ExpectedItemId,
                            request.ItemCount,
                            out deletion);
                    }
                    if (!consumed
                        || (!request.ConsumeByTemplateId
                            && !InventoryService.IsVirtualMainSlot(request.SlotIndex)
                            && (deletion == null || !deletion.Success)))
                    {
                        return false;
                    }

                    if (!request.ConsumeByTemplateId
                        && !InventoryService.IsVirtualMainSlot(request.SlotIndex))
                        inventoryChanges = deletion.Changes;

                    applied = _repository.TryApplyGift(
                        connection,
                        transaction,
                        lease.CharacterId,
                        request.NpcId,
                        request.DefaultFavorPoint,
                        request.GameDayId,
                        request.ItemCount,
                        request.FavorPointDelta,
                        request.NpcDailyGiftLimit,
                        request.MaximumFavorPoint,
                        request.TrustedFavorPoint);
                    return applied.Success;
                });

            if (!committed)
            {
                if (applied?.Error == NpcFavorGiftApplyError.NpcDailyLimit)
                    return Fail(NpcFavorGiftError.NpcDailyLimit);
                return Fail(NpcFavorGiftError.CommitFailed);
            }

            return new NpcFavorGiftResult
            {
                Success = true,
                Error = NpcFavorGiftError.None,
                InventoryDeletion = deletion,
                InventoryChanges = inventoryChanges,
                State = applied.State,
                AppliedFavorPointDelta = applied.AppliedFavorPointDelta,
            };
        }

        private static NpcFavorGiftResult Fail(NpcFavorGiftError error)
            => new NpcFavorGiftResult
            {
                Success = false,
                Error = error,
            };
    }
}
