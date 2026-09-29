using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.NpcFavor;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class NpcFavorHandler
    {
        // Captured A21 gift packets identify the character main/material space
        // with 0x24. Other operation/space layouts remain unverified.
        private const byte CapturedGiftSpace = 0x24;

        private readonly NpcFavorGiftCommandService _gift;
        private readonly InventoryRefreshSender _refresh;

        internal NpcFavorHandler(
            IGameDatabase database,
            InventoryRefreshSender refresh)
            : this(
                NpcFavorDefinitionCatalog.Load(PvfArchiveAccessor.ReadText),
                database,
                refresh)
        {
        }

        internal NpcFavorHandler(
            NpcFavorDefinitionCatalog catalog,
            IGameDatabase database,
            InventoryRefreshSender refresh)
        {
            var repository = new NpcFavorRepository(
                database ?? throw new ArgumentNullException(nameof(database)));
            _gift = new NpcFavorGiftCommandService(catalog, repository);
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        internal void RegisterHandlers(
            GameCommandRegistry.GameCommandRegistrationGroup registrations)
        {
            registrations[(ushort)CmdPacketTypeA21.REQUEST_NPC_FAVOR_OPERATION] =
                Handle;
        }

        internal async Task Handle(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!NpcFavorOperationRequestCodec.TryParse(body, out var request)
                || request.Operation != NpcFavorOperationRequestCodec.GiftOperation
                || request.RawListType != CapturedGiftSpace
                || session?.Player == null)
            {
                FileLogger.Log(
                    $"[NpcFavor] reject malformed/unverified request: "
                    + $"cid={session?.Player?.CharacterId ?? 0} "
                    + $"body({body?.Length ?? 0})="
                    + (body == null ? "null" : BitConverter.ToString(body)));
                return;
            }

            var player = session.Player;
            if (!InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log(
                    $"[NpcFavor] reject missing owned inventory: "
                    + $"cid={player.CharacterId} npc={request.NpcId}");
                return;
            }

            var result = _gift.TryGift(
                lease,
                player.Level,
                request.NpcId,
                request.SlotIndex,
                DailyResetService.TodayId());
            if (!result.Success)
            {
                if (NpcFavorOperationAckBuilder.TryBuildGiftFailure(
                        result.Gift?.Error ?? NpcFavorGiftError.None,
                        out var failureBody))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        header.type,
                        failureBody));
                }

                FileLogger.Log(
                    $"[NpcFavor] gift rejected: cid={player.CharacterId} "
                    + $"npc={request.NpcId} slot={request.SlotIndex} "
                    + $"plan={result.Plan?.Error} gift={result.Gift?.Error}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                NpcFavorOperationAckBuilder.BuildGift(
                    request.NpcId,
                    result.Gift.AppliedFavorPointDelta,
                    result.Gift.State.FavorPoint,
                    result.Plan.Disposition)));

            foreach (var list in result.Gift.InventoryChanges.Slots
                .GroupBy(change => change.ListType))
            {
                await _refresh.SendUpdateItemList(
                    session,
                    list.Key,
                    list.Select(change => change.SlotIndex));
            }

            FileLogger.Log(
                $"[NpcFavor] gift committed: cid={player.CharacterId} "
                + $"npc={request.NpcId} item={result.Plan.Request.ExpectedItemId} "
                + $"count={result.Plan.Request.ItemCount} "
                + $"favor={result.Gift.State.FavorPoint} "
                + $"dailyActions={result.Gift.State.GiftActionCount}");
        }
    }
}
