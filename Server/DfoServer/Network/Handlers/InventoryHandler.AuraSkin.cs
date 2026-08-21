using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_OPEN_AURA_SKIN_SLOT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryReadAuraSkinMaterialSlot(body, out var materialSlot))
            {
                await SendAuraSkinSlotOpenError(session, header, 0x04);
                return;
            }

            if (materialSlot < InventoryService.MainSlotStart
                || materialSlot > InventoryService.MainSlotEnd)
            {
                await SendAuraSkinSlotOpenError(session, header, 0x02);
                return;
            }

            var (characterId, _) = ResolveOwner(session);
            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] OPEN_AURA_SKIN_SLOT: inventory lease missing "
                    + $"characterId={characterId} session={session?.SessionId}");
                await SendAuraSkinSlotOpenError(session, header, 0x04);
                return;
            }

            InventoryMutationResult consumedMaterial = null;
            var alreadyOpen = false;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "open-aura-skin-slot",
                (connection, transaction) =>
                {
                    if (lease.Inventory.IsAuraSkinSlotOpened)
                    {
                        alreadyOpen = true;
                        return true;
                    }

                    if (!InventoryDeleteService.TryDeleteForClient(
                            lease.Inventory,
                            InventoryListType.Main,
                            materialSlot,
                            1,
                            out consumedMaterial)
                        || consumedMaterial == null)
                    {
                        return false;
                    }

                    if (!SqliteCharacterRepository.UpdateAuraSkinFlagInTransaction(
                            connection,
                            transaction,
                            characterId,
                            1))
                    {
                        return false;
                    }

                    lease.Inventory.SetAuraSkinFlag(1);
                    return true;
                });

            if (!committed)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] OPEN_AURA_SKIN_SLOT: failed "
                    + $"characterId={characterId} materialSlot={materialSlot}");
                await SendAuraSkinSlotOpenError(session, header, 0x04);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                CommonPacketBodyBuilder.BuildSuccessAck()));

            if (consumedMaterial != null)
                await _refresh.SendUpdateItemList(
                    session,
                    consumedMaterial.ListType,
                    consumedMaterial.SlotIndex);

            await _refresh.SendUserInfoSubtype1Refresh(
                session,
                "OPEN_AURA_SKIN_SLOT");

            FileLogger.Log(
                $"[{ProtocolName}] OPEN_AURA_SKIN_SLOT: OK "
                + $"characterId={characterId} materialSlot={materialSlot} "
                + $"alreadyOpen={alreadyOpen} remaining={consumedMaterial?.RemainingStackCount ?? -1}");
        }

        private static bool TryReadAuraSkinMaterialSlot(
            byte[] body,
            out short materialSlot)
        {
            materialSlot = 0;
            if (body == null || body.Length < 4)
                return false;

            var value = BitConverter.ToInt32(body, 0);
            if (value < short.MinValue || value > short.MaxValue)
                return false;

            materialSlot = (short)value;
            return true;
        }

        private static Task SendAuraSkinSlotOpenError(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte errorCode)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                CommonPacketBodyBuilder.BuildCmdError(errorCode)));
        }
    }
}
