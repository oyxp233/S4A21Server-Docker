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
        public async Task Handle_UPGRADE_CHRONICLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ChronicleGrowthRequest.TryParse(body, out var command))
            {
                FileLogger.Log($"[{ProtocolName}] UPGRADE_CHRONICLE: parse failed body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, (ushort)CmdPacketType.UPGRADE_CHRONICLE,
                    ChronicleGrowthAckBuilder.BuildError(ChronicleGrowthResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] UPGRADE_CHRONICLE ticket=({command.TicketSlotIndex},0x{command.TicketItemTemplateId:X8}) target=({command.TargetSlotIndex},0x{command.TargetItemTemplateId:X8}) materials={string.Join(",", command.Materials.Select(x => $"({x.SlotIndex},0x{x.ItemTemplateId:X8})"))}");

            var (characterId, _) = ResolveOwner(session);
            ChronicleGrowthResult result = null;
            InventoryLease lease = null;
            var isEmancipate = false;
            var ok = TryGetOwnedInventoryLease(session, characterId, out lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "chronicle-growth",
                    (connection, transaction) =>
                    {
                        if (ItemMetadataResolver.TryLoadStackableFile(command.TicketItemTemplateId, out var ticket)
                            && IsEquipmentConversionTicket(ticket.EmancipateTicket)
                            && ticket.EmancipateTicket >= 0
                            && ItemMetadataResolver.TryLoadEquipmentFile(command.TargetItemTemplateId, out var target)
                            && target.Emancipate != null
                            && target.Emancipate.Type == ticket.EmancipateTicket)
                        {
                            isEmancipate = true;
                            return InventoryEmancipateService.TryConvert(lease.Inventory, command, out result);
                        }

                        var grown = ChronicleGrowthService.TryGrow(lease.Inventory, command, out result);
                        return grown && result != null;
                    });

            if (!ok)
            {
                var errorCode = result?.ErrorCode ?? ChronicleGrowthResult.ErrorInvalidRequest;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, (ushort)CmdPacketType.UPGRADE_CHRONICLE,
                    ChronicleGrowthAckBuilder.BuildError(errorCode)));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_CHRONICLE: FAILED error=0x{errorCode:X2}");
                return;
            }

            var refreshSlots = new[] { command.TargetSlotIndex }
                .Concat(result.Consumptions.Select(x => x.SlotIndex))
                .Distinct()
                .ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, (ushort)CmdPacketType.UPGRADE_CHRONICLE,
                ChronicleGrowthAckBuilder.BuildSuccess(result)));

            // A21 replacement flows first clear the old target entry with a
            // single-slot 0x000E, then send the authoritative multi-slot
            // update. This invalidates the client's cached source equipment.
            if (isEmancipate)
                await _refresh.SendEmptyUpdateItemList(session, InventoryListType.Main, command.TargetSlotIndex);

            // Send the authoritative multi-slot 0x000E update after the
            // 0x010F ACK. The A21 client may rebuild the slot from the ACK
            // handling path, so sending the update first can leave stale data
            // visible until the next inventory sort/refresh.
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, refreshSlots);

            FileLogger.Log($"[{ProtocolName}] UPGRADE_CHRONICLE: OK mode={(isEmancipate ? "emancipate" : "growth")} target={command.TargetSlotIndex} level={result.OldLevel}->{result.NewLevel} success={result.GrowthSucceeded} fragments={result.RequiredFragmentCount} roll={result.ProbabilityRoll}/{result.SuccessWeight}");
        }

        private static bool IsEquipmentConversionTicket(int ticket)
        {
            // Keep the existing growth/改造 path for PVF tickets handled by
            // ChronicleGrowthService. This branch is only for equipment
            // conversion records with an [emancipate] source definition.
            return ticket >= 4 && ticket != 5;
        }
    }
}
