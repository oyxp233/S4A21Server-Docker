using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_AVATAR_OPTION_CHANGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_OPTION_CHANGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!AvatarOptionChangeRequestParser.TryParse(body, out var request))
            {
                await SendAvatarOptionChangeError(session);
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            InventoryAvatarOptionChangeResult result = null;
            InventoryLease lease = null;
            var ok = TryGetOwnedInventoryLease(session, characterId, out lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "avatar-option-change",
                    (connection, transaction) =>
                    {
                        var changed = InventoryAvatarOptionChangeService.TryChange(
                            lease.Inventory,
                            request,
                            out result);
                        return changed && result != null && result.Success;
                    });

            if (!ok || result == null || !result.Success)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] AVATAR_OPTION_CHANGE: FAILED error={result?.Error} " +
                    $"source=({request.SourceSlotIndex},0x{request.SourceItemId:X8}) " +
                    $"target=({request.TargetSlotIndex},0x{request.TargetItemId:X8}) abilityNo={request.AbilityNo}");
                await SendAvatarOptionChangeError(session);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x01CC,
                AvatarOptionChangeAckBuilder.BuildSuccess(
                    request.TargetSlotIndex,
                    result.AbilityNo)));

            await _refresh.SendUpdateItemList(session, InventoryListType.Main, request.SourceSlotIndex);

            FileLogger.Log(
                $"[{ProtocolName}] AVATAR_OPTION_CHANGE: OK " +
                $"source=({request.SourceSlotIndex},0x{result.SourceItemId:X8}) remaining={result.SourceRemainingCount} " +
                $"target=({request.TargetSlotIndex},0x{result.TargetItemId:X8}) abilityNo={result.AbilityNo}");
        }

        private static Task SendAvatarOptionChangeError(EnhancedClientSession session)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x01CC,
                AvatarOptionChangeAckBuilder.BuildError()));
        }
    }
}
