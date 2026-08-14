using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_UPGRADE_ITEM_SEPARATE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!SeparateUpgradeRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, SeparateUpgradeAckBuilder.BuildError(SeparateUpgradeResult.ErrorInvalidTarget)));
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, SeparateUpgradeAckBuilder.BuildError(SeparateUpgradeResult.ErrorInvalidTarget)));
                return;
            }

            SeparateUpgradeTable table;
            ItemMetadata metadata;
            try
            {
                table = SeparateUpgradeTableProvider.Get();
                metadata = ItemMetadataResolver.Resolve(request.TargetItemTemplateId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM_SEPARATE: PVF load failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, SeparateUpgradeAckBuilder.BuildError(SeparateUpgradeResult.ErrorUnsupported)));
                return;
            }

            var ok = InventoryRecipeSeparateUpgradeCommitService.TryCommitSeparateUpgrade(
                lease,
                request.ToCommand(),
                table,
                metadata,
                out var result,
                out var persistenceFailed);

            if (!ok)
            {
                var error = persistenceFailed
                    ? SeparateUpgradeResult.ErrorMaterialCommit
                    : result?.ErrorCode ?? SeparateUpgradeResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM_SEPARATE: FAILED error={error} persistenceFailed={persistenceFailed} target=({request.TargetListType},{request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, SeparateUpgradeAckBuilder.BuildError(error)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, header.type, SeparateUpgradeAckBuilder.BuildSuccess(result)));
            if (request.TargetListType == InventoryListType.Main)
            {
                await _refresh.SendUpdateItemList(session, InventoryListType.Main,
                    new[] { request.TargetSlotIndex, request.MaterialSlotIndex });
            }
            else
            {
                await _refresh.SendUpdateItemList(session, request.TargetListType, request.TargetSlotIndex);
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, request.MaterialSlotIndex);
            }
            if (result.UpgradeSucceeded && request.TargetListType == InventoryListType.Equipment)
                await _refresh.SendNoti2AppearanceUpdate(session);
            if (result.NoticeRequired)
            {
                await BroadcastItemNotice(
                    session,
                    "UPGRADE_ITEM_SEPARATE",
                    userUniqueId => SeparateUpgradeNoticeBuilder.Build(result, userUniqueId),
                    $"item=0x{request.TargetItemTemplateId:X8} level={result.NewLevel}");
            }

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM_SEPARATE: OK target=({request.TargetListType},{request.TargetSlotIndex}) level={result.OldLevel}->{result.NewLevel} success={result.UpgradeSucceeded} rate={result.SuccessWeight}/10000 material=0x{result.MaterialItemTemplateId:X8} cost={result.MaterialCost} remaining={result.MaterialRemainingCount}");
        }
    }
}
