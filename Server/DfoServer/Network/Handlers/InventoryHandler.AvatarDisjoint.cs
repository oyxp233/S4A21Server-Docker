using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_DISJOINT_AVATAR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!AvatarDisjointRequestParser.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                    AvatarDisjointAckBuilder.BuildError(AvatarDisjointResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] DISJOINT_AVATAR raw({body?.Length ?? 0}B): {(body == null ? "null" : BitConverter.ToString(body))} slot={request.SlotIndex} expected=0x{request.ExpectedItemTemplateId:X8}");
            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                    AvatarDisjointAckBuilder.BuildError(AvatarDisjointResult.ErrorInvalidRequest)));
                return;
            }

            bool hasRewardSpace;
            int freeEmblemSlots;
            ItemSlotRange emblemRange;
            AvatarDisjointResult result = null;
            lock (lease.SyncRoot)
            {
                hasRewardSpace = InventorySpaceCheckService.HasEnoughAvatarEmblemFreeSlots(
                    lease.Inventory,
                    out freeEmblemSlots,
                    out emblemRange);
            }

            if (!hasRewardSpace)
            {
                FileLogger.Log($"[{ProtocolName}] DISJOINT_AVATAR: FAILED emblem free slots insufficient free={freeEmblemSlots} required={InventorySpaceCheckService.RequiredFreeAvatarDisjointRewardSlots} range={emblemRange.Start}-{emblemRange.End}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                    AvatarDisjointAckBuilder.BuildError(AvatarDisjointResult.ErrorInventoryFull)));
                return;
            }

            var ok = InventoryDisjointCommitService.TryCommitAvatar(
                lease,
                request,
                out result,
                out var persistenceFailed);

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? AvatarDisjointResult.ErrorInvalidRequest
                    : result?.ErrorCode ?? AvatarDisjointResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] DISJOINT_AVATAR: FAILED error=0x{errorCode:X2} persistenceFailed={persistenceFailed} slot={request.SlotIndex} expected=0x{request.ExpectedItemTemplateId:X8}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                    AvatarDisjointAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                AvatarDisjointAckBuilder.BuildSuccess(result)));

            await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, request.SlotIndex);
            var mainSlots = new List<short>();
            foreach (var material in result.Materials)
                mainSlots.Add(material.SlotIndex);
            if (mainSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, mainSlots);

            FileLogger.Log($"[{ProtocolName}] DISJOINT_AVATAR OK source=0x{result.SourceItemTemplateId:X8} slot={request.SlotIndex} rewards={result.Materials.Count} ack=0x00CA-native");
        }
    }
}
