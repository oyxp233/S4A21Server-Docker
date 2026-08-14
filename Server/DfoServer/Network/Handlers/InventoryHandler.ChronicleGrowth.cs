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
            var ok = TryGetOwnedInventoryLease(session, characterId, out lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "chronicle-growth",
                    (connection, transaction) =>
                    {
                        var grown = ChronicleGrowthService.TryGrow(
                            lease.Inventory,
                            command,
                            out result);
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

            var refreshSlots = result.Consumptions.Select(x => x.SlotIndex)
                .Append(command.TargetSlotIndex)
                .Distinct()
                .ToArray();
            // The 0x010F ACK has no target level/stat payload. Refresh first so the
            // result dialog can compare the updated equipment and render stat gains.
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, refreshSlots);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, (ushort)CmdPacketType.UPGRADE_CHRONICLE,
                ChronicleGrowthAckBuilder.BuildSuccess(result)));

            await _refresh.SendSortItemLockRefresh(session, InventoryListType.Main);

            FileLogger.Log($"[{ProtocolName}] UPGRADE_CHRONICLE: OK target={command.TargetSlotIndex} level={result.OldLevel}->{result.NewLevel} success={result.GrowthSucceeded} fragments={result.RequiredFragmentCount} roll={result.ProbabilityRoll}/{result.SuccessWeight}");
        }
    }
}
