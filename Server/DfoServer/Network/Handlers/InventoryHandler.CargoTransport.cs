using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_CARGO_TRANSPORT_ITEM(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!CargoTransportStoneRequestParser.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CargoTransportItemAckBuilder.BuildError(null)));
                FileLogger.Log(
                    $"[{ProtocolName}] CARGO_TRANSPORT_ITEM rejected malformed body({body?.Length ?? 0}B)");
                return;
            }

            var (characterId, accountId) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                await SendCargoTransportAck(session, header.type, request, null);
                return;
            }

            var targetCharacter = request.IsCreatureTransportStone
                ? ResolveCargoTransportTargetCharacter(accountId, request.TargetCharacterSlotIndex)
                : null;
            var ok = InventoryCargoTransportStoneCommitService.TryCommit(
                lease,
                request,
                targetCharacter,
                _mailboxService,
                out var result,
                out var persistenceFailed);
            if (persistenceFailed && result != null)
            {
                result.Status = CargoTransportStoneStatus.MutationFailed;
                result.Detail = "inventory persistence failed";
            }

            await SendCargoTransportAck(session, header.type, request, result);
            await SendCargoTransportRefreshes(session, lease, result);

            FileLogger.Log(
                $"[{ProtocolName}] CARGO_TRANSPORT_ITEM result={result?.Status} "
                + $"ok={ok} persistenceFailed={persistenceFailed} "
                + $"stoneSlot={request.StoneSlotIndex} targetSlot={request.TargetSlotIndex} "
                + $"pet={request.IsCreatureTransportStone} characterSlot={request.TargetCharacterSlotIndex} "
                + $"stone=0x{(result?.StoneItemTemplateId ?? 0):X8} "
                + $"target=0x{(result?.TargetItemTemplateId ?? 0):X8} "
                + $"stoneType={result?.StoneType ?? -1} detail={result?.Detail}");
        }

        private async Task SendCargoTransportAck(
            EnhancedClientSession session,
            ushort packetType,
            CargoTransportStoneRequest request,
            CargoTransportStoneResult result)
        {
            var body = result != null
                ? CargoTransportItemAckBuilder.Build(result)
                : CargoTransportItemAckBuilder.BuildError(request);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                body));
        }

        private async Task SendCargoTransportRefreshes(
            EnhancedClientSession session,
            InventoryLease lease,
            CargoTransportStoneResult result)
        {
            if (session == null || result == null)
                return;

            if (result.Success)
            {
                if (result.PetRefreshSlots.Count > 0)
                    await _refresh.SendUpdateItemList(
                        session,
                        InventoryListType.Pet,
                        result.PetRefreshSlots);

                RecalibrateCargoTransportQuestProgress(session, lease, result);
                return;
            }

            if (result.MainRefreshSlots.Count > 0)
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    result.MainRefreshSlots);

            if (result.AccountCargoRefreshSlots.Count > 0)
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.AccountCargo,
                    result.AccountCargoRefreshSlots);

            if (result.PetRefreshSlots.Count > 0)
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Pet,
                    result.PetRefreshSlots);

            if (result.CreatureListChanged)
                await _refresh.SendCreatureItemListRefresh(session);

            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.UsableCountState);

            RecalibrateCargoTransportQuestProgress(session, lease, result);
        }

        private static void RecalibrateCargoTransportQuestProgress(
            EnhancedClientSession session,
            InventoryLease lease,
            CargoTransportStoneResult result)
        {
            var mutations = BuildCargoTransportQuestMutations(result);
            if (mutations.Count > 0)
            {
                session.GameSession?.QuestManager
                    ?.RecalibrateItemSeekingQuestProgressAfterInventoryMutationsWithoutNotification(
                        lease,
                        mutations);
            }
        }

        private static IReadOnlyList<InventoryMutationResult> BuildCargoTransportQuestMutations(
            CargoTransportStoneResult result)
        {
            var mutations = new List<InventoryMutationResult>();
            if (result?.StoneMutation != null)
                mutations.Add(result.StoneMutation);
            if (result?.TargetMutation != null
                && result.TargetMutation.ListType == InventoryListType.Main)
            {
                mutations.Add(result.TargetMutation);
            }

            return mutations;
        }

        private CharacterRecord ResolveCargoTransportTargetCharacter(
            int accountId,
            int targetCharacterSlotIndex)
        {
            if (accountId <= 0 || targetCharacterSlotIndex < 0)
                return null;

            var characters = _characterRepository
                .ListByAccount(accountId)
                .Where(character => character != null && !character.Deleted)
                .OrderBy(character => character.SlotIndex)
                .ThenBy(character => character.CharacterId)
                .ToList();
            return targetCharacterSlotIndex < characters.Count
                ? characters[targetCharacterSlotIndex]
                : null;
        }
    }
}
