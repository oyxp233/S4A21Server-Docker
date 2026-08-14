using System;
using System.Threading.Tasks;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 租赁商店租赁武器。幸运星扣减直接写账号状态，物品写在线背包。
    /// </summary>
    public sealed class RentalHandler
    {
        private readonly IRentalTimeProvider _rentalTimeProvider;
        private readonly SqliteSelectCharacterDataSource _dataSource;
        private readonly InventoryRefreshSender _refresh;

        public RentalHandler(
            SqliteSelectCharacterDataSource dataSource,
            IRentalTimeProvider rentalTimeProvider = null,
            InventoryRefreshSender refresh = null,
            IGameDatabase database = null)
        {
            _dataSource = dataSource;
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            _refresh = refresh;
        }

        public async Task HandleRentWeapon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0 || accountId <= 0)
                return;

            if (!RentalWeaponRequestCodec.TryParse(body, out var weaponId, out var parsedInventoryId, out var starCost, out var priceTier))
            {
                var tail = body == null || body.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(body, Math.Max(0, body.Length - 8));
                var head = body == null || body.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(body, 0, Math.Min(13, body.Length));
                var detail = RentalWeaponRequestCodec.DescribeParseFailure(body);
                FileLogger.Log($"[Rental] REJECT 0x0372 char={characterId} parse failed bodyLen={body?.Length ?? 0} head={head} tail={tail} detail={detail}");
                await Send0372Error(session);
                return;
            }

            if (!InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[Rental] RENT_WEAPON: online inventory missing shop=0x{weaponId:X8} inv=0x{parsedInventoryId:X8} char={characterId}");
                await Send0372Error(session);
                return;
            }

            var inventoryTemplateId = (int)parsedInventoryId;
            var expireTime = (int)ResolveRentalExpireTime();
            var luckyStar = (ushort)0;
            InventoryMutationResult rentResult = null;
            string rejectLog = null;

            var success = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "rental-weapon-purchase",
                (connection, transaction) =>
                {
                    if (!InventoryShopRuntimeService.CanRentWeapon(lease.Inventory, inventoryTemplateId))
                    {
                        rejectLog = $"[Rental] RENT_WEAPON: plan FAILED (inventory full or invalid) shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} char={characterId}";
                        return false;
                    }

                    var currentLuckyStar = CurrencyService.LoadWallet(connection, transaction, characterId).LuckyStar;
                    if (!CurrencyService.TrySpendLuckyStar(connection, transaction, accountId, starCost))
                    {
                        rejectLog = $"[Rental] RENT_WEAPON: insufficient stars need={starCost} have={currentLuckyStar} char={characterId}";
                        return false;
                    }

                    var rental = _dataSource.LoadRentalInfo(
                        connection,
                        transaction,
                        characterId);
                    rental.UpsertItem(weaponId, (uint)inventoryTemplateId, (uint)expireTime);
                    _dataSource.SaveRentalInfo(connection, transaction, characterId, rental);
                    luckyStar = (ushort)Math.Max(0, currentLuckyStar - starCost);

                    if (!InventoryShopRuntimeService.TryRentWeapon(
                            lease.Inventory,
                            inventoryTemplateId,
                            expireTime,
                            out rentResult,
                            connection,
                            transaction))
                    {
                        rejectLog = $"[Rental] RENT_WEAPON: apply FAILED shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} char={characterId}";
                        return false;
                    }

                    return true;
                });

            if (!success || rentResult == null)
            {
                FileLogger.Log(rejectLog ?? $"[Rental] RENT_WEAPON: failed shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} char={characterId}");
                await Send0372Error(session);
                return;
            }

            FileLogger.Log($"[Rental] RENT_WEAPON: added/refreshed shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} list={rentResult.ListType} slot={rentResult.SlotIndex} char={characterId}");
            FileLogger.Log($"[Rental] RENT_WEAPON: char={characterId} weapon=0x{weaponId:X8} inv=0x{parsedInventoryId:X8} cost={starCost} priceTier={priceTier} starsLeft={luckyStar} expire={expireTime}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0372, CommonPacketBodyBuilder.BuildSuccessAck()));
            await RentalInfoPanelNotifier.SyncAsync(session, _dataSource, characterId, luckyStar, _rentalTimeProvider);

            if (_refresh != null && rentResult.SlotIndex >= 0)
                await _refresh.SendUpdateItemList(session, rentResult.ListType, rentResult.SlotIndex);
        }

        private RentalInfoSnapshot LoadRentalInfo(int characterId)
        {
            return _dataSource.LoadRentalInfo(characterId);
        }

        private static async Task Send0372Error(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0372, new byte[] { 0x00, 0x04 }));
        }

        private uint ResolveRentalExpireTime()
        {
            return _rentalTimeProvider.UtcNowUnixSeconds() + (uint)RentalWeaponRequestCodec.RentalDurationSeconds;
        }
    }
}
