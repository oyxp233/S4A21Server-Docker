using System;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_USE_DYE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] USE_DYE raw({body?.Length ?? 0}B): "
                + $"{(body != null ? BitConverter.ToString(body) : "null")}");

            if (!UseDyeRequestParser.TryParse(body, out var request))
            {
                await SendUseDyeError(session, header.type);
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendUseDyeError(session, header.type);
                return;
            }

            InventoryDyeResult result = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "use-dye",
                (connection, transaction) => InventoryDyeService.TryUse(
                    lease.Inventory,
                    request,
                    InventoryItemLifecycleService.UtcNowUnixSeconds(),
                    out result));

            if (!committed || result == null || !result.Success)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_DYE: FAILED committed={committed} "
                    + $"error={result?.Error} dyeSlot={request.DyeSlotIndex} "
                    + $"avatarSlot={request.AvatarSlotIndex} dye=0x{(result?.DyeItemTemplateId ?? 0):X8} "
                    + $"avatar=0x{(result?.AvatarItemTemplateId ?? 0):X8}");
                await SendUseDyeError(session, header.type);
                if (committed)
                    await SendUseDyeRefreshes(session, result);
                return;
            }

            await SendUseDyeRefreshes(session, result);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                DyeItemAckBuilder.BuildSuccess(
                    result.Request.AvatarSlotIndex,
                    result.Color1,
                    result.Color2)));

            FileLogger.Log(
                $"[{ProtocolName}] USE_DYE: OK dyeSlot={request.DyeSlotIndex} "
                + $"avatarSlot={request.AvatarSlotIndex} dye=0x{result.DyeItemTemplateId:X8} "
                + $"avatar=0x{result.AvatarItemTemplateId:X8} color1={result.Color1} "
                + $"color2={result.Color2} "
                + $"remaining={result.DyeRemainingCount}");
        }

        private static Task SendUseDyeError(
            EnhancedClientSession session,
            ushort packetType)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                DyeItemAckBuilder.BuildError()));
        }

        private async Task SendUseDyeRefreshes(
            EnhancedClientSession session,
            InventoryDyeResult result)
        {
            if (session == null || result?.Changes == null)
                return;

            foreach (var slot in result.Changes.Slots)
            {
                if (!ShouldSendUseDyeRefresh(result, slot))
                    continue;

                await _refresh.SendUpdateItemList(session, slot.ListType, slot.SlotIndex);
            }
        }

        internal static bool ShouldSendUseDyeRefresh(
            InventoryDyeResult result,
            InventorySlotMutation slot)
        {
            // 0x01F3 ACK 的染色信息块负责刷新目标时装，这里只刷新被消耗的染色剂槽位。
            return result?.Request != null
                && slot.ListType == InventoryListType.Main
                && slot.SlotIndex == result.Request.DyeSlotIndex;
        }
    }
}
