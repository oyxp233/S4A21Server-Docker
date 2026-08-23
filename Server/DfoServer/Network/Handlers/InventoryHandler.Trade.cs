using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.TitleBook;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_DELETE_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4)
                return;

            if (TryBuildDungeonDeleteItemResponsePlan(
                    session?.Player?.CurrentRun?.RewardPolicy,
                    body,
                    out var rejectionBody,
                    out var rejectedListType))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0012,
                    rejectionBody));
                FileLogger.Log(
                    $"[{ProtocolName}] DELETE_ITEM training rejected: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"listType={rejectedListType}");
                return;
            }

            var (cid, _) = ResolveOwner(session);
            var hasInventoryLease = TryGetOwnedInventoryLease(session, cid, out var lease);


            if (body.Length >= 15 && body[1] >= 1 && body[1] <= 100)
            {
                var listType = (InventoryListType)body[0];
                var arrayCount = body[1];
                var offset = 2;
                var mutations = new List<InventoryMutationResult>();
                var achievementProgress =
                    new SortedDictionary<int, AchievementTriggerResult>();

                // Entry (12B): opType(u16) + slotIndex(u16) + itemId(i32) + deleteCount(i32)
                for (int i = 0; i < arrayCount && offset + 12 <= body.Length; i++)
                {
                    var opType = BitConverter.ToUInt16(body, offset);
                    var slotIndex = BitConverter.ToInt16(body, offset + 2);
                    var itemId = BitConverter.ToInt32(body, offset + 4);
                    var deleteCount = (short)BitConverter.ToInt32(body, offset + 8);
                    offset += 12;

                    InventoryMutationResult result = null;
                    var deleted = false;
                    if (hasInventoryLease)
                    {
                        if (IsSkillMaterialDeleteOperation(opType))
                        {
                            IReadOnlyList<AchievementTriggerResult> entryProgress =
                                Array.Empty<AchievementTriggerResult>();
                            deleted = InventoryDeleteCommitService.TryCommit(
                                lease,
                                listType,
                                slotIndex,
                                deleteCount,
                                "delete-skill-material",
                                committedMutation =>
                                {
                                    var actualDeletedCount = Math.Max(
                                        0,
                                        (int)(committedMutation?.AppliedCount ?? 0));
                                    if (actualDeletedCount > 0)
                                    {
                                        entryProgress = _sqliteSelectCharacterDataSource
                                            .TriggerUseItemAchievements(
                                                lease,
                                                committedMutation.ItemTemplateId,
                                                actualDeletedCount);
                                    }

                                    return true;
                                },
                                out result);

                            if (deleted)
                                MergeAchievementProgress(
                                    achievementProgress,
                                    entryProgress);
                        }
                        else
                        {
                            deleted = InventoryDeleteCommitService.TryCommit(
                                lease,
                                listType,
                                slotIndex,
                                deleteCount,
                                out result);
                        }
                    }

                    if (!deleted)
                    {
                        FileLogger.Log($"[{ProtocolName}] DELETE_ITEM(ext): failed at listType={listType} slot={slotIndex} count={deleteCount}");
                        var errAck = DeleteItemAckBuilder.BuildError((byte)listType);
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, errAck));
                        continue;
                    }

                    result.AppliedCount = deleteCount;
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, DeleteItemAckBuilder.Build(result)));
                    mutations.Add(result);
                    FileLogger.Log($"[{ProtocolName}] DELETE_ITEM(ext): op={opType} slot={slotIndex} item=0x{itemId:X8} applied={deleteCount} remaining={result.RemainingStackCount}");
                }

                if (hasInventoryLease
                    && mutations.Count > 0
                    && session.GameSession?.QuestManager != null)
                {
                    session.GameSession.QuestManager
                        .RecalibrateItemSeekingQuestProgressAfterInventoryMutationsWithoutNotification(
                            lease,
                            mutations);
                }

                foreach (var progress in achievementProgress.Values)
                {
                    await SendAchievementTriggerResult(
                        session,
                        cid,
                        (ushort)CmdPacketTypeA21.ACHIEVEMENT_TRIGGER,
                        progress);
                }
                return;
            }


            if (!TryParseDeleteOrSellRequest(body, out var lt, out var si, out var ic))
                return;

            InventoryMutationResult simpleResult = null;
            var simpleDeleted = false;
            if (hasInventoryLease)
            {
                simpleDeleted = InventoryDeleteCommitService.TryCommit(
                    lease,
                    lt,
                    si,
                    ic,
                    out simpleResult);
            }

            if (!simpleDeleted)
            {
                var errAck = DeleteItemAckBuilder.BuildError((byte)lt);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, errAck));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, DeleteItemAckBuilder.Build(simpleResult)));
            if (hasInventoryLease && session.GameSession?.QuestManager != null)
            {
                session.GameSession.QuestManager
                    .RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                        lease,
                        simpleResult);
            }
        }

        internal static bool TryBuildDungeonDeleteItemResponsePlan(
            DungeonRewardPolicy rewardPolicy,
            byte[] body,
            out byte[] rejectionBody,
            out InventoryListType listType)
        {
            rejectionBody = null;
            listType = InventoryListType.Main;
            if (body == null
                || body.Length < 4
                || DungeonInteractionPolicy.Resolve(rewardPolicy)
                    .AllowsItemDiscard)
            {
                return false;
            }

            listType = body.Length >= 15
                ? (InventoryListType)body[0]
                : TryParseDeleteOrSellRequest(
                    body,
                    out var parsedListType,
                    out _,
                    out _)
                    ? parsedListType
                    : InventoryListType.Main;
            rejectionBody = DeleteItemAckBuilder.BuildError((byte)listType);
            return true;
        }

        internal static bool IsSkillMaterialDeleteOperation(ushort operationType)
            => operationType > 1;

        internal static void MergeAchievementProgress(
            IDictionary<int, AchievementTriggerResult> target,
            IEnumerable<AchievementTriggerResult> incoming)
        {
            if (target == null || incoming == null)
                return;

            foreach (var result in incoming)
            {
                if (result?.Success != true)
                    continue;

                if (!target.TryGetValue(result.QuestId, out var current)
                    || !current.Completed
                    || result.Completed)
                {
                    target[result.QuestId] = result;
                }
            }
        }

        public async Task Handle_ENUM_CMDPACKET_BUY_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4)
                return;

            var itemTemplateId = BitConverter.ToInt32(body, 0);
            var buyCount = body.Length >= 8 ? BitConverter.ToInt32(body, 4) : 1;
            var shopId = body.Length >= 12 ? BitConverter.ToInt32(body, 8) : 0;
            var npcId = body.Length >= 16 ? BitConverter.ToInt32(body, 12) : 0;
            if (buyCount <= 0) buyCount = 1;
            FileLogger.Log(
                $"[{ProtocolName}] BUY_ITEM: itemTemplateId=0x{itemTemplateId:X8} "
                + $"count={buyCount} shopId={shopId} npcId={npcId}");

            var (cid, _) = ResolveOwner(session);
            InventoryMutationResult result = null;
            bool ok;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "npc-buy-item",
                    (connection, transaction) =>
                        InventoryShopRuntimeService.TryBuyNpcItem(
                            lease.Inventory,
                            itemTemplateId,
                            buyCount,
                            npcId,
                            connection,
                            transaction,
                            out result));
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: FAILED itemTemplateId=0x{itemTemplateId:X8}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0015, BuyItemAckBuilder.BuildError(0x00)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] BUY_ITEM: OK slot={result.SlotIndex} gold={result.UpdatedGold} sp={result.UpdatedSp} coin={result.UpdatedCoin} expire={result.ExpireTime} costId={result.CostItemTemplateId} costRemain={result.CostItemRemainingCount}");
            if (result.CostItemTemplateId > 0)
            {
                if (result.CostItemRemainingCount <= 0
                    && result.ListType == InventoryListType.Main
                    && result.SlotIndex == result.CostItemSlotIndex)
                    await _refresh.SendEmptyUpdateItemList(session, InventoryListType.Main, result.CostItemSlotIndex);
                else
                    await _refresh.SendUpdateItemList(session, InventoryListType.Main, result.CostItemSlotIndex);
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: ACK cost item slot={result.CostItemSlotIndex} id=0x{result.CostItemTemplateId:X8} remain={result.CostItemRemainingCount}");
            }

            var purchaseCountUpdates = new List<PurchaseCountUpdate>
            {
                new PurchaseCountUpdate
                {
                    ItemTemplateId = result.ItemTemplateId,
                    RequestedCount = buyCount,
                }
            };

            var ackBody = BuyItemAckBuilder.Build(result, purchaseCountUpdates);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0015, ackBody));

            if (result.GoldSpent)
                await _refresh.SendGoldUpdate(session);

            if (result.ListType == InventoryListType.Main)
                await _refresh.SendUpdateItemList(session, result.ListType, result.SlotIndex);

            if (result.ListType != InventoryListType.Main)
            {
                await _refresh.SendUpdateItemList(session, result.ListType, result.SlotIndex);
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: ITEM_LIST update sent list={result.ListType} slot={result.SlotIndex}");
            }

            if (session.GameSession?.QuestManager != null)
            {
                await session.GameSession.QuestManager
                    .SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                        lease,
                        result);
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SHOP_PURCHASE_COUNT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length < 4)
                return;

            var npcId = BitConverter.ToInt32(body, 0);
            if (npcId <= 0)
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            var counts = ItemPurchaseLimitService.LoadNpcPurchaseCounts(
                lease.Inventory,
                npcId);
            if (counts == null || counts.Count == 0)
                return;

            FileLogger.Log(
                $"[{ProtocolName}] SHOP_PURCHASE_COUNT: npcId={npcId} items={counts.Count}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x02CC,
                ShopPurchaseCountPacketBuilder.Build(counts)));
        }

        public async Task Handle_ENUM_CMDPACKET_SELL_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] SELL_ITEM raw body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseDeleteOrSellRequest(body, out var listType, out var slotIndex, out var sellCount))
                return;

            FileLogger.Log($"[{ProtocolName}] SELL_ITEM: listType={listType}({(byte)listType}) slot={slotIndex} count={sellCount}");

            var (cid, _) = ResolveOwner(session);
            InventoryMutationResult result = null;
            var code = 1;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "npc-sell-item",
                    (connection, transaction) =>
                    {
                        code = InventoryShopRuntimeService.TrySellItem(
                            lease.Inventory,
                            listType,
                            slotIndex,
                            sellCount,
                            connection,
                            transaction,
                            out result);
                        return code == 0;
                    });
                if (!committed && code == 0)
                {
                    code = 1;
                    result = null;
                }
            }

            if (code != 0)
            {
                FileLogger.Log($"[{ProtocolName}] SELL_ITEM: FAILED listType={listType} slot={slotIndex} count={sellCount}");

                // 60 公告 个人商店开设中，无法出售物品
                // 77 公告 无法与加入黑名单的角色进行聊天
                // 105 公告 为了保护您的账号安全， 登录 0 秒后才可以交易
                // 114 公告 处于限制交易状态，系统将取消相关请求
                // 115 公告 对方处于限制交易状态，无法进行物品交易
                // 134 公告 因需要二级密码认证，此次操作被取消，请重新操作
                // 206 公告 您被禁闭在赛丽亚旅馆，无法进行该操作
                // 208 公告 为了保护长时间未登录的休眠账号，系统将限制您游戏内的各类交易和技能学习。若申请二级密码、手机令牌、密保卡其中之一，即可正常使用。
                // 0xd2 您的账户现在处于安全模式中，不能进行任何的交易、丢弃等敏感操作。如需解除安全模式，请点击下方的确定解除按钮。
                // 0xd6 公告 在物品已锁定状态下，您无法进行当前操作。
                // 0xe5 长时间断开连接会有盗号风险，为了保护游戏内财务安全，您的部分交易功能已被限制。 为了保护账号而对您造成的不便，请您谅解，并请在本人确认后使用。 **韩服**
                // 0xe6 公告 您目前处于限制交易状态，无法使用该功能。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0016, SellItemBuilder.BuildError((byte)code)));
                if (code == 22)
                {
                    var writer = new GamePacketWriter();
                    writer.WriteByte(3);
                    writer.WriteUInt32(0);
                    writer.WriteUInt32(0);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketType.MONEY_FULL, writer.ToArray()));
                }
                return;
            }

            FileLogger.Log($"[{ProtocolName}] SELL_ITEM: OK gold={result.UpdatedGold} applied={result.AppliedCount}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0016, SellItemBuilder.Build((byte)listType, result.SlotIndex, result.AppliedCount, result.UpdatedGold)));
            if (session.GameSession?.QuestManager != null)
            {
                session.GameSession.QuestManager
                    .RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                        lease,
                        result);
            }
        }

        public async Task Handle_SET_CLONE_TITLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cloneTitle = (body != null && body.Length >= 4) ? BitConverter.ToInt32(body, 0) : 0;
            var (cid, _) = ResolveOwner(session);
            Game.Appearance.AppearanceService.SaveCloneTitleItemId(
                cid,
                cloneTitle,
                _database);
            if (session.Player != null)
            {
                var tail = session.Player.Subtype0Tail ?? new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                tail.CloneTitleItemId = (uint)(cloneTitle > 0 ? cloneTitle : 0);
                session.Player.Subtype0Tail = tail;
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0239,
                Game.Appearance.AppearanceService.BuildCloneTitleAckBody(cloneTitle)));
            FileLogger.Log($"[{ProtocolName}] SET_CLONE_TITLE: cloneTitle=0x{cloneTitle:X8} persisted, ackExtra=00-00");
        }

        public async Task Handle_TITLE_BOOK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 20)
                return;

            var itemSpaceRaw = BitConverter.ToInt32(body, 0);
            var slot = (short)BitConverter.ToInt32(body, 4);
            var itemId = BitConverter.ToInt32(body, 8);
            var category = BitConverter.ToInt32(body, 12);
            var index = BitConverter.ToInt32(body, 16);
            var opName = header.type == 0x019C ? "PUT" : "GET";
            FileLogger.Log($"[{ProtocolName}] TITLE_BOOK_{opName}: itemSpace={itemSpaceRaw} slot={slot} item=0x{itemId:X8} category={category} index={index}");

            if (!TryParseInventoryListType(itemSpaceRaw, out var itemSpace))
            {
                FileLogger.Log($"[{ProtocolName}] TITLE_BOOK_{opName}: invalid itemSpace={itemSpaceRaw}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, BuildTitleBookFailure(itemSpaceRaw, category, 0x0A)));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    cid,
                    out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    BuildTitleBookFailure(itemSpaceRaw, category, 0x0A)));
                return;
            }

            bool ok;
            Game.TitleBook.TitleBookMutationResult result;
            if (header.type == (ushort)CmdPacketTypeA21.TITLE_BOOK_PUT)
                ok = _sqliteSelectCharacterDataSource.TryPutTitleBook(
                    lease,
                    aid,
                    itemSpace,
                    slot,
                    itemId,
                    category,
                    index,
                    out result);
            else
                ok = _sqliteSelectCharacterDataSource.TryGetTitleBook(
                    lease,
                    aid,
                    itemSpace,
                    slot,
                    itemId,
                    category,
                    index,
                    out result);

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] TITLE_BOOK_{opName}: FAILED itemSpace={itemSpace} slot={slot} item=0x{itemId:X8} category={category} index={index} error=0x{(result != null ? result.ErrorCode : (byte)0x0A):X2}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, BuildTitleBookFailure(itemSpaceRaw, category, result != null ? result.ErrorCode : (byte)0x0A)));
                return;
            }

            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "title-book-" + opName.ToLowerInvariant()))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TITLE_BOOK_{opName}: commit failed "
                    + $"cid={cid} item=0x{itemId:X8} "
                    + $"category={result.Category} index={result.BookIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    BuildTitleBookFailure(itemSpaceRaw, category, 0x0A)));
                return;
            }

            if (result.EquipmentChanged)
            {
                _refresh.ReloadSubtype0Tail(session);
                await _refresh.SendNoti2AppearanceUpdate(session);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, header.type, BuildTitleBookSuccess(itemSpaceRaw, slot, result.Category, result.BookIndex)));

            if (result.ItemLockChanged)
                await _refresh.SendAllEquipmentItemLockListRefresh(session);
        }

        public async Task Handle_ACHIEVEMENT_TRIGGER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 10)
                return;

            var questId = BitConverter.ToInt32(body, 0);
            var delta1 = BitConverter.ToUInt16(body, 4);
            var delta2 = BitConverter.ToUInt16(body, 6);
            var delta3 = BitConverter.ToUInt16(body, 8);
            var (cid, _) = ResolveOwner(session);

            if (!InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    cid,
                    out var lease))
            {
                await SendAchievementFailure(session, header.type, questId, 0x0A);
                return;
            }

            if (!_sqliteSelectCharacterDataSource.TryTriggerAchievement(
                    lease,
                    questId,
                    delta1,
                    delta2,
                    delta3,
                    out var result))
            {
                await SendAchievementFailure(
                    session,
                    header.type,
                    questId,
                    result != null ? result.ErrorCode : (byte)0x0A);
                return;
            }

            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "achievement-trigger"))
            {
                await SendAchievementFailure(session, header.type, questId, 0x0A);
                return;
            }

            await SendAchievementTriggerResult(
                session,
                cid,
                header.type,
                result);
        }

        private async Task SendAchievementTriggerResult(
            EnhancedClientSession session,
            int characterId,
            ushort commandType,
            AchievementTriggerResult result)
        {
            if (result?.Success != true)
                return;

            var progress = new GamePacketWriter();
            progress.WriteByte(1);
            progress.WriteInt32(result.QuestId);
            progress.WriteUInt16(result.Remain1);
            progress.WriteUInt16(result.Remain2);
            progress.WriteUInt16(result.Remain3);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                commandType,
                progress.ToArray()));

            if (result.Completed && result.TitleItemId > 0)
            {
                var complete = new GamePacketWriter();
                complete.WriteInt32(result.QuestId);
                complete.WriteInt32(result.Category);
                complete.WriteInt32(result.BookIndex);
                complete.WriteInt32(result.TitleItemId);
                complete.WriteUInt16((ushort)Math.Max(0, result.BookIndex));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.ACHIEVEMENT_COMPLETE,
                    complete.ToArray()));
                await SendTitleBookCategoryRefresh(
                    session,
                    characterId,
                    result.Category);
            }
        }

        private static Task SendAchievementFailure(
            EnhancedClientSession session,
            ushort commandType,
            int questId,
            byte errorCode)
        {
            var fail = new GamePacketWriter();
            fail.WriteByte(0);
            fail.WriteByte(errorCode);
            fail.WriteInt32(questId);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                commandType,
                fail.ToArray()));
        }

        private static byte[] BuildTitleBookSuccess(int itemSpace, short slot, int category, int index)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteInt32(itemSpace);
            w.WriteInt32(slot);
            w.WriteInt32(category);
            w.WriteInt32(index);
            return w.ToArray();
        }

        private static byte[] BuildTitleBookFailure(int itemSpace, int category, byte errorCode)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0);
            w.WriteByte(errorCode);
            w.WriteInt32(itemSpace);
            w.WriteInt32(category);
            return w.ToArray();
        }

        private async Task SendTitleBookCategoryRefresh(EnhancedClientSession session, int characterId, int category)
        {
            var snapshot = _sqliteSelectCharacterDataSource.LoadTitleBookSnapshot(characterId, category);
            if (snapshot == null)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.TITLE_BOOK_LIST,
                TitleBookListBodyBuilder.BuildCategoryBody(snapshot)));
        }

        private static bool TryParseInventoryListType(int value, out InventoryListType listType)
        {
            if (value >= byte.MinValue && value <= byte.MaxValue && Enum.IsDefined(typeof(InventoryListType), (byte)value))
            {
                listType = (InventoryListType)(byte)value;
                return true;
            }

            listType = InventoryListType.Main;
            return false;
        }

        // ── 账号金库 ──────────────────────────────────────────────────────────
        // SQL 已下沉到新背包持久化层；handler 只留解析和 ACK。

        public async Task Handle_DEPOSIT_MONEY(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await HandleCargoGold(session, header.type, body, isDeposit: true);
        }

        public async Task Handle_WITHDRAW_MONEY(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await HandleCargoGold(session, header.type, body, isDeposit: false);
        }

        private async Task HandleCargoGold(EnhancedClientSession session, ushort wireType, byte[] body, bool isDeposit)
        {
            if (body == null || body.Length < 4)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                return;
            }

            int amount = BitConverter.ToInt32(body, 0);
            if (amount <= 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            int newCharGold = 0, newCargoGold = 0;
            InventoryMutationResult mutation = null;
            InventoryLease lease;
            var ok = TryGetOwnedInventoryLease(session, cid, out lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    isDeposit ? "account-cargo-deposit-gold" : "account-cargo-withdraw-gold",
                    (connection, transaction) => isDeposit
                        ? InventoryCargoRuntimeService.TryDepositCargoGold(
                            lease.Inventory,
                            amount,
                            out newCharGold,
                            out newCargoGold,
                            out mutation)
                        : InventoryCargoRuntimeService.TryWithdrawCargoGold(
                            lease.Inventory,
                            amount,
                            out newCharGold,
                            out newCargoGold,
                            out mutation));
            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                return;
            }

            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);
            ack.WriteInt32(newCargoGold);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, ack.ToArray()));

            await _refresh.SendGoldUpdate(session);
            if (session.GameSession?.QuestManager != null)
            {
                await session.GameSession.QuestManager
                    .SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
                        lease,
                        mutation);
            }

            FileLogger.Log($"[{ProtocolName}] {(isDeposit ? "DEPOSIT" : "WITHDRAW")}_MONEY: amount={amount} charGold={newCharGold} cargoGold={newCargoGold}");
        }

        public async Task Handle_CREATE_ACCOUNT_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            InventoryMutationResult costResult;
            byte errorCode;
            costResult = null;
            errorCode = 0x14;
            var ok = TryGetOwnedInventoryLease(session, cid, out var lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "account-cargo-create",
                    (connection, transaction) => InventoryCargoRuntimeService.TryCreateAccountCargo(
                        lease.Inventory,
                        out costResult,
                        out errorCode));

            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0131, new byte[] { 0x00, errorCode }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0131, new byte[] { 0x01 }));
            await SendAccountCargoCostRefresh(session, costResult);
            await _refresh.SendItemListRefresh(session, InventoryListType.AccountCargo);
            FileLogger.Log($"[{ProtocolName}] CREATE_ACCOUNT_CARGO: aid={aid} cargo created costGold={costResult?.GoldSpent == true} costItem=0x{costResult?.CostItemTemplateId ?? 0:X8}");
        }

        public async Task Handle_UPGRADE_ACCOUNT_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            InventoryMutationResult costResult;
            byte errorCode;
            costResult = null;
            errorCode = 0x15;
            var ok = TryGetOwnedInventoryLease(session, cid, out var lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "account-cargo-upgrade",
                    (connection, transaction) => InventoryCargoRuntimeService.TryUpgradeAccountCargo(
                        lease.Inventory,
                        connection,
                        transaction,
                        out costResult,
                        out errorCode));

            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x00, errorCode }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x01 }));
            await SendAccountCargoCostRefresh(session, costResult);
            await _refresh.SendItemListRefresh(session, InventoryListType.AccountCargo);
            FileLogger.Log($"[{ProtocolName}] UPGRADE_ACCOUNT_CARGO: aid={aid} selectionKey upgraded costGold={costResult?.GoldSpent == true} costItem=0x{costResult?.CostItemTemplateId ?? 0:X8} costItemRemain={costResult?.CostItemRemainingCount ?? 0} coin={costResult?.UpdatedCoin ?? 0}");
        }

        private async Task SendAccountCargoCostRefresh(EnhancedClientSession session, InventoryMutationResult costResult)
        {
            if (costResult == null)
                return;

            if (costResult.GoldSpent)
            {
                await _refresh.SendGoldUpdate(session);
                return;
            }

            if (costResult.CostItemTemplateId > 0)
            {
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, costResult.CostItemSlotIndex);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0035,
                CeraUpdateBuilder.Build(
                    costResult.UpdatedCoin,
                    costResult.UpdatedTokenCera,
                    costResult.UpdatedHappyTokenCera)));
        }

        public async Task Handle_UPGRADE_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            ushort newListParam16;
            byte errorCode;
            newListParam16 = 0;
            errorCode = 0x13;
            var ok = TryGetOwnedInventoryLease(session, cid, out var lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "personal-cargo-upgrade",
                    (connection, transaction) => InventoryCargoRuntimeService.TryUpgradePersonalCargo(
                        lease.Inventory,
                        out newListParam16,
                        out errorCode));

            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, errorCode }));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_CARGO: failed cid={cid} aid={aid} error=0x{errorCode:X2} rawBody({body?.Length ?? 0}B)={(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x01 }));
            await _refresh.SendItemListRefresh(session, InventoryListType.PersonalCargo);
            FileLogger.Log($"[{ProtocolName}] UPGRADE_CARGO: cid={cid} aid={aid} personalCargoListParam16={newListParam16} rawBody({body?.Length ?? 0}B)={(body != null ? BitConverter.ToString(body) : "null")}");
        }
    }
}
