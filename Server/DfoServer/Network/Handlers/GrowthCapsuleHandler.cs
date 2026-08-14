using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class GrowthCapsuleHandler
    {
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly GrowthCapsuleSyncService _sync;
        private readonly GrowthCapsuleClaimService _claimService;

        public GrowthCapsuleHandler(
            InventoryRefreshSender inventoryRefresh,
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            _inventoryRefresh = inventoryRefresh ?? throw new ArgumentNullException(nameof(inventoryRefresh));
            database ??= GameDatabase.CreateDefault();
            _claimService = new GrowthCapsuleClaimService(database);
            _sync = new GrowthCapsuleSyncService(
                characterRepository ?? throw new ArgumentNullException(nameof(characterRepository)),
                database);
        }

        public async Task HandleClaimAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var accountId = session?.Account?.AccountId ?? 0;
            var characterId = session?.Player?.CharacterId ?? 0;
            if (accountId <= 0
                || characterId <= 0
                || session.Player.Level < ExpTableProvider.MaxLevel)
            {
                await SendFailureAckAsync(session);
                return;
            }

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendFailureAckAsync(session);
                return;
            }

            var claim = _claimService.Claim(lease);
            if (!claim.Success)
            {
                FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_CLAIM rejected: account={accountId} cid={characterId} status={claim.Status} total={claim.Summary.TotalExp} required={claim.Summary.RequiredExp}");
                await SendFailureAckAsync(session);
                // 86JP 处理任意领取 ACK 后都会清零本地胶囊值，失败时也要恢复服务端进度。
                await _sync.SendExpProgressAsync(session, $"claim-{claim.Status}", claim.Summary);
                return;
            }

            FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_CLAIM ok: account={accountId} cid={characterId} item={claim.ItemId} count={claim.ItemCount} slot={claim.AssignedSlot} remain={claim.Summary.TotalExp}");
            await SendSuccessAckAsync(
                session, claim.ItemId, claim.ItemCount);
            await _inventoryRefresh.SendUpdateItemList(session, InventoryListType.Main, claim.AssignedSlot);
            await _sync.SendExpProgressAsync(session, "claim-success", claim.Summary);
        }

        private static Task SendFailureAckAsync(EnhancedClientSession session)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.GET_EXPAND_EXP_GAGE_REWARD,
                GrowthCapsulePacketBuilder.BuildClaimAck(false)));
        }

        private static Task SendSuccessAckAsync(
            EnhancedClientSession session,
            int itemId,
            int itemCount)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.GET_EXPAND_EXP_GAGE_REWARD,
                GrowthCapsulePacketBuilder.BuildClaimAck(true, itemId, itemCount)));
        }
    }
}
