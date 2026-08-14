using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class GoldLimitHandler
    {
        private readonly CharacterGoldLimitRepository _repository;
        private readonly InventoryRefreshSender _refresh;

        public GoldLimitHandler(CharacterGoldLimitRepository repository, InventoryRefreshSender refresh = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _refresh = refresh;
        }

        public async Task HandleUpgradeAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return;

            var result = _repository.TryUpgrade(characterId);
            FileLogger.Log(
                $"[GoldLimit] upgrade cid={characterId} status={result.Status} " +
                $"level={result.Limits?.UpgradeLevel ?? 0} carry={result.Limits?.GoldCarryLimit ?? 0} " +
                $"auction={result.Limits?.AuctionGoldLimit ?? 0} goldAfter={result.GoldAfter}");

            if (result.Limits != null)
            {
                // 0x035D 的 CMD 响应处理器属于收益制裁数据，不是本面板状态；
                // 扩充结果由 0x0331 通知更新，避免把错误包体送进该处理器。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00, 0x0331, new[] { result.Limits.UpgradeLevel }));
            }

            if (result.Status == GoldLimitUpgradeStatus.Success)
            {
                if (InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    characterId,
                    out var lease))
                {
                    lock (lease.SyncRoot)
                        lease.Inventory.AttachMainVirtualCount(
                            InventoryService.MainVirtualCurrencySlotStart,
                            InventoryService.MainVirtualCurrencySlotStart,
                            result.GoldAfter);
                }

                if (_refresh != null)
                    await _refresh.SendGoldUpdate(session);
            }
        }
    }
}
