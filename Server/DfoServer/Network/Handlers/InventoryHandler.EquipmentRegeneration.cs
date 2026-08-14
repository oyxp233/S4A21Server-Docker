using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_REGENERATION_RANDOM_OPTION(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0, 17 }));
                return;
            }

            var request = new EquipmentRegenerationRequest
            {
                SourceSlotIndex = BitConverter.ToInt16(body, 0),
                Mode = BitConverter.ToUInt16(body, 2),
                Part = BitConverter.ToUInt16(body, 4),
            };
            var (cid, _) = ResolveOwner(session);
            EquipmentRegenerationResult result = null;
            var persistenceFailed = false;
            var ok = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                try
                {
                    ok = InventoryEquipmentRegenerationCommitService.TryCommit(
                        lease,
                        request,
                        out result,
                        out persistenceFailed);
                }
                catch (Exception ex)
                {
                    result = new EquipmentRegenerationResult
                    {
                        SourceSlotIndex = request.SourceSlotIndex,
                        Mode = request.Mode,
                        Part = request.Part,
                    };
                    FileLogger.Log($"[{ProtocolName}] REGENERATION_RANDOM_OPTION failed cid={cid} sourceSlot={request.SourceSlotIndex} mode={request.Mode} part={request.Part}: {ex}");
                }
            }
            else
            {
                result = new EquipmentRegenerationResult();
            }

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] REGENERATION_RANDOM_OPTION rejected cid={cid} sourceSlot={request.SourceSlotIndex} mode={request.Mode} part={request.Part} persistenceFailed={persistenceFailed} error={result?.ErrorCode ?? (byte)17}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0, result?.ErrorCode ?? (byte)17 }));
                return;
            }

            var refresh = new List<short> { result.SourceSlotIndex };
            if (result.ResultSlotIndex >= 0 && !refresh.Contains(result.ResultSlotIndex))
                refresh.Add(result.ResultSlotIndex);
            foreach (var consumed in result.ConsumedEntries)
            {
                if (consumed.SlotIndex >= 0 && !refresh.Contains(consumed.SlotIndex))
                    refresh.Add(consumed.SlotIndex);
            }
            if (result.ResultSlotIndex == result.SourceSlotIndex)
            {
                // A final-state update alone does not make the 86JP client
                // replace an equipment template already cached in this slot.
                // Project the removal before the final-state item update.
                await _refresh.SendEmptyUpdateItemList(
                    session,
                    InventoryListType.Main,
                    result.SourceSlotIndex);
            }
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, refresh);

            // The client resolves this slot immediately for the result popup.
            var ack = new GamePacketWriter();
            ack.WriteByte(1);
            ack.WriteUInt16(checked((ushort)result.ResultSlotIndex));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ack.ToArray()));

            FileLogger.Log($"[{ProtocolName}] REGENERATION_RANDOM_OPTION ok cid={cid} source=0x{result.SourceItemTemplateId:X8}@{result.SourceSlotIndex} result=0x{result.ResultItemTemplateId:X8}@{result.ResultSlotIndex} mode={result.Mode} part={result.Part} level={result.TargetLevel} legacy={result.LegacyResult} candidates={result.CandidateCount} weight={result.SelectedWeight:0.####} materials={string.Join(',', result.ConsumedEntries.Select(entry => $"{entry.ItemTemplateId}:{entry.Count}@{entry.SlotIndex}"))}");
        }
    }
}
