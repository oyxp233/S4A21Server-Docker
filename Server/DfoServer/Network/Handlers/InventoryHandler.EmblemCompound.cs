using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_COMPOUND_EMBLEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!EmblemCompoundRequestParser.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0100,
                    EmblemCompoundAckBuilder.BuildError(EmblemCompoundResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] COMPOUND_EMBLEM raw({body.Length}B): {BitConverter.ToString(body)} " +
                $"inputs={string.Join(",", request.Inputs.Select(input => $"0x{input.ItemTemplateId:X8}@{input.SlotIndex}"))}");

            var (characterId, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0100,
                    EmblemCompoundAckBuilder.BuildError(EmblemCompoundResult.ErrorInvalidRequest)));
                return;
            }

            var ok = InventoryCardEmblemCommitService.TryCommitEmblemCompound(
                lease,
                request,
                out var result,
                out var persistenceFailed);

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? EmblemCompoundResult.ErrorInvalidRequest
                    : result?.ErrorCode ?? EmblemCompoundResult.ErrorInvalidRequest;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0100,
                    EmblemCompoundAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0100,
                EmblemCompoundAckBuilder.BuildSuccess(result)));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, result.ChangedSlots);

            FileLogger.Log($"[{ProtocolName}] COMPOUND_EMBLEM OK booster=0x{result.PvfBoosterItemTemplateId:X8} " +
                $"reward=0x{result.RewardItemTemplateId:X8}@{result.RewardSlotIndex} " +
                $"granted={result.RewardGrantedCount} stack={result.RewardStackCount} " +
                $"ack=CMD0100({EmblemCompoundAckBuilder.SuccessLength}B result-list)+UPDATE_ITEM_LIST " +
                $"slots={string.Join(",", result.ChangedSlots)}");
        }
    }
}
