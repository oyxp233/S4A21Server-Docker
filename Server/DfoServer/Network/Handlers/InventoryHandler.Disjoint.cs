using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_DISJOINT_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!DisjointItemRequestParser.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x001A,
                    DisjointItemAckBuilder.BuildError(DisjointItemResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} target=({request.ItemSpace},{request.TargetSlotIndex}) disjointSlot={request.DisjointItemSlotIndex} ctx=0x{request.ContextValue:X8}");

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001A,
                    DisjointItemAckBuilder.BuildError(DisjointItemResult.ErrorInvalidTarget)));
                return;
            }

            bool hasRewardSpace;
            int freeMaterialSlots;
            ItemSlotRange materialRange;
            DisjointItemResult result = null;
            lock (lease.SyncRoot)
            {
                hasRewardSpace = InventorySpaceCheckService.HasEnoughMaterialFreeSlots(
                    lease.Inventory,
                    out freeMaterialSlots,
                    out materialRange);
            }

            if (!hasRewardSpace)
            {
                FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM: FAILED material free slots insufficient free={freeMaterialSlots} required={InventorySpaceCheckService.RequiredFreeMaterialRewardSlots} range={materialRange.Start}-{materialRange.End}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001A,
                    DisjointItemAckBuilder.BuildError(DisjointItemResult.ErrorInventoryFull)));
                return;
            }

            var ok = InventoryDisjointCommitService.TryCommitItem(
                lease,
                request,
                out result,
                out var persistenceFailed);

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? DisjointItemResult.ErrorInvalidTarget
                    : result != null
                        ? result.ErrorCode
                        : DisjointItemResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM: FAILED error=0x{errorCode:X2} persistenceFailed={persistenceFailed} target=({request.ItemSpace},{request.TargetSlotIndex})");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001A, DisjointItemAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001A, DisjointItemAckBuilder.BuildSuccess(result)));
            // 分解 ACK 自带目标槽位和产物 slot/item/count，客户端据此刷新，不额外补 0x000E。
            var questManager = session.GameSession?.QuestManager;
            if (questManager != null)
            {
                await questManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                    lease,
                    result.InventoryMutations);
            }

            var materialText = result.Materials.Count > 0
                ? string.Join(", ", result.Materials.ConvertAll(m => $"0x{m.ItemTemplateId:X8}x{m.Count}@{m.SlotIndex}"))
                : string.Empty;
            FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM: OK source=0x{result.SourceItemTemplateId:X8} targetSlot={request.TargetSlotIndex} results={result.Materials.Count} materials=[{materialText}]");
        }
    }
}
