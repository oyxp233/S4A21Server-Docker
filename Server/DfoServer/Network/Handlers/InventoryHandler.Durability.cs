using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_DECREASE_DURABILITY(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!DecreaseDurabilityRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DECREASE_DURABILITY invalid body({body?.Length ?? 0}B): "
                    + $"{(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    DecreaseDurabilityAckBuilder.BuildError(
                        DecreaseDurabilityAckBuilder.ErrorInvalidTarget)));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            EquipmentDurabilityDecreaseResult result = null;
            bool committed;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "decrease-durability",
                    (connection, transaction) =>
                        InventoryDurabilityService.TryDecreaseEquippedDurability(
                            lease.Inventory,
                            request.EquipmentSlotIndex,
                            out result));
            }
            else
            {
                committed = false;
            }

            if (!committed)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DECREASE_DURABILITY failed "
                    + $"slot={request.EquipmentSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    DecreaseDurabilityAckBuilder.BuildError(
                        DecreaseDurabilityAckBuilder.ErrorInvalidTarget)));
                return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] DECREASE_DURABILITY "
                + $"slot={request.EquipmentSlotIndex} "
                + $"changed={result?.Changed ?? false} "
                + $"item={result?.ItemTemplateId ?? 0} "
                + $"durability={result?.PreviousDurability ?? 0}->{result?.CurrentDurability ?? 0} "
                + $"reason={result?.Reason ?? "unknown"}");

            if (result != null && (result.Changed || result.Reason == "zero_durability"))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    DecreaseDurabilityAckBuilder.BuildSuccess(result.SlotIndex)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                DecreaseDurabilityAckBuilder.BuildError(
                    DecreaseDurabilityAckBuilder.ErrorInvalidTarget)));
        }
    }
}
