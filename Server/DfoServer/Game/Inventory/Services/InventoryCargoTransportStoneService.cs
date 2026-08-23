using DfoServer.Game.Characters;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCargoTransportStoneService
    {
        private const string CargoActionType = "cargo transport stone";
        private const string CreatureActionType = "creature transport stone";
        private const ushort SourceProtocol = 0x022E;

        internal static bool TryUse(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            CargoTransportStoneRequest request,
            CharacterRecord targetCharacter,
            MailboxService mailboxService,
            out CargoTransportStoneResult result)
        {
            result = CreateResult(request);
            if (connection == null || transaction == null || inventory == null || request == null)
                return Fail(result, CargoTransportStoneStatus.InvalidRequest, "invalid request", true);

            if (request.StoneSlotIndex < 0 || request.TargetSlotIndex < 0)
                return Fail(result, CargoTransportStoneStatus.InvalidRequest, "negative slot index", true);

            if (!InventoryDeleteService.CanUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    request.StoneSlotIndex,
                    0,
                    out var stoneItemId))
            {
                return Fail(result, CargoTransportStoneStatus.SourceMissing, "stone slot is unavailable", true);
            }

            result.StoneItemTemplateId = stoneItemId;
            var now = InventoryItemLifecycleService.UtcNowUnixSeconds();
            if (InventoryItemLifecycleService.TryRemoveExpiredSource(
                    inventory,
                    InventoryListType.Main,
                    request.StoneSlotIndex,
                    stoneItemId,
                    now,
                    out var expiredMutation))
            {
                result.Status = CargoTransportStoneStatus.SourceExpired;
                result.Detail = "stone item has expired";
                result.SourceExpiredDeleted = true;
                result.StoneMutation = expiredMutation;
                AddUnique(result.MainRefreshSlots, request.StoneSlotIndex);
                return true;
            }

            var stackable = StackableItemProvider.Load(stoneItemId);
            if (!TryResolveStoneDefinition(
                    request,
                    stackable,
                    result,
                    out var definition))
            {
                return true;
            }

            var lifecyclePlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                request.StoneSlotIndex,
                stoneItemId,
                now,
                1,
                stackable,
                checkEffectMaintenance: false,
                checkCooltimeMaintenance: true);
            if (!lifecyclePlan.Success)
            {
                ApplyLifecycleFailure(result, lifecyclePlan);
                return true;
            }

            if (request.IsCreatureTransportStone)
            {
                if (targetCharacter == null
                    || targetCharacter.Deleted
                    || targetCharacter.AccountId != inventory.AccountId)
                {
                    return Fail(result, CargoTransportStoneStatus.TargetCharacterMissing, "target character is unavailable", true);
                }

                result.TargetCharacterId = targetCharacter.CharacterId;
                if (!ValidateTarget(
                        inventory,
                        InventoryListType.Pet,
                        request.TargetSlotIndex,
                        expectedKind: ItemCore.KindCreature,
                        definition,
                        stackable,
                        forceEquipmentType: EquipmentType.Creature,
                        result,
                        out var target))
                {
                    return true;
                }

                if (!ConsumeStoneAndRecordUse(
                        connection,
                        transaction,
                        inventory,
                        request,
                        stoneItemId,
                        result,
                        out var usableCountState))
                {
                    return result.Status != CargoTransportStoneStatus.MutationFailed;
                }

                if (!TransferCreatureByMail(
                        connection,
                        transaction,
                        inventory,
                        request,
                        targetCharacter,
                        target,
                        mailboxService,
                        result))
                {
                    return false;
                }

                result.UsableCountState = usableCountState;
                InventoryItemLifecycleService.ApplyUseSuccess(inventory, lifecyclePlan);
                result.Status = CargoTransportStoneStatus.Success;
                result.AckParameter = request.TargetCharacterSlotIndex;
                result.AckMode = 1;
                return true;
            }

            if (!ValidateTarget(
                    inventory,
                    InventoryListType.Main,
                    request.TargetSlotIndex,
                    expectedKind: ItemCore.KindEquipment,
                    definition,
                    stackable,
                    forceEquipmentType: null,
                    result,
                    out var equipment))
            {
                return true;
            }

            if (equipment.EquipmentLockId != 0)
                return Fail(result, CargoTransportStoneStatus.TargetLocked, "target equipment is locked", true);

            if (!TryFindAccountCargoSlot(inventory, out var cargoSlot))
                return Fail(result, CargoTransportStoneStatus.AccountCargoFull, "account cargo is full", true);

            if (!ConsumeStoneAndRecordUse(
                    connection,
                    transaction,
                    inventory,
                    request,
                    stoneItemId,
                    result,
                    out var equipmentUsableCountState))
            {
                return result.Status != CargoTransportStoneStatus.MutationFailed;
            }

            if (!TransferEquipmentToAccountCargo(
                    inventory,
                    request,
                    equipment,
                    cargoSlot,
                    result))
            {
                return false;
            }

            result.UsableCountState = equipmentUsableCountState;
            InventoryItemLifecycleService.ApplyUseSuccess(inventory, lifecyclePlan);
            result.Status = CargoTransportStoneStatus.Success;
            result.AccountCargoSlotIndex = cargoSlot;
            result.AckParameter = cargoSlot;
            result.AckMode = 0;
            return true;
        }

        private static CargoTransportStoneResult CreateResult(
            CargoTransportStoneRequest request)
        {
            return new CargoTransportStoneResult
            {
                Request = request ?? new CargoTransportStoneRequest(),
            };
        }

        private static bool TryResolveStoneDefinition(
            CargoTransportStoneRequest request,
            StackableItemFile stackable,
            CargoTransportStoneResult result,
            out CargoTransportStoneDefinition definition)
        {
            definition = null;
            if (stackable == null)
                return Fail(result, CargoTransportStoneStatus.InvalidStone, "stone stackable file is missing", false);

            var expectedAction = request.IsCreatureTransportStone
                ? CreatureActionType
                : CargoActionType;
            var action = NormalizeActionType(stackable.ActionTypeName);
            if (!string.Equals(action, expectedAction, StringComparison.OrdinalIgnoreCase))
                return Fail(result, CargoTransportStoneStatus.InvalidStone, "stone action type mismatch", false);

            if (!stackable.ActionTypeParams.Any(value => value >= 0))
                return Fail(result, CargoTransportStoneStatus.InvalidStone, "stone action type parameter is missing", false);

            var stoneType = stackable.ActionTypeParams.First(value => value >= 0);
            result.StoneType = stoneType;
            if (!CargoTransportStoneConfigProvider.TryGetDefinition(stoneType, out definition))
                return Fail(result, CargoTransportStoneStatus.InvalidStone, "stone type config is missing", false);

            return true;
        }

        private static void ApplyLifecycleFailure(
            CargoTransportStoneResult result,
            InventoryItemLifecycleUsePlan lifecyclePlan)
        {
            if (lifecyclePlan == null)
            {
                Fail(result, CargoTransportStoneStatus.InvalidLifecycle, "lifecycle plan is missing", false);
                return;
            }

            if (lifecyclePlan.Status == InventoryItemLifecycleStatus.CooltimeActive)
                Fail(result, CargoTransportStoneStatus.CooltimeActive, lifecyclePlan.Detail, false);
            else if (lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceChanged)
                Fail(result, CargoTransportStoneStatus.SourceChanged, lifecyclePlan.Detail, false);
            else if (lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceEmpty)
                Fail(result, CargoTransportStoneStatus.SourceEmpty, lifecyclePlan.Detail, false);
            else if (lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceMissing)
                Fail(result, CargoTransportStoneStatus.SourceMissing, lifecyclePlan.Detail, false);
            else if (lifecyclePlan.Status == InventoryItemLifecycleStatus.SourceExpired)
                Fail(result, CargoTransportStoneStatus.SourceExpired, lifecyclePlan.Detail, false);
            else
                Fail(result, CargoTransportStoneStatus.InvalidLifecycle, lifecyclePlan.Detail, false);
        }

        private static bool ValidateTarget(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            byte expectedKind,
            CargoTransportStoneDefinition definition,
            StackableItemFile stackable,
            EquipmentType? forceEquipmentType,
            CargoTransportStoneResult result,
            out ItemCore target)
        {
            target = inventory.GetItem(listType, slotIndex);
            if (target == null || target.ItemId <= 0)
                return Fail(result, CargoTransportStoneStatus.TargetMissing, "target slot is empty", false);

            result.TargetItemTemplateId = target.ItemId;
            if (target.ItemKind != expectedKind)
                return Fail(result, CargoTransportStoneStatus.TargetInvalidKind, "target item kind is invalid", false);

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(target.ItemId);
            }
            catch
            {
                metadata = null;
            }

            if (metadata == null)
                return Fail(result, CargoTransportStoneStatus.TargetNotAllowed, "target metadata is missing", false);

            var equipmentType = forceEquipmentType
                ?? EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType);
            if (!definition.AllowsLevel(metadata.MinimumLevel)
                || !definition.AllowsEquipmentType(equipmentType)
                || !AllowsRarity(stackable, metadata.Rarity)
                || !definition.AllowsItemId(target.ItemId))
            {
                return Fail(result, CargoTransportStoneStatus.TargetNotAllowed, "target is not allowed by stone config", false);
            }

            return true;
        }

        private static bool ConsumeStoneAndRecordUse(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            CargoTransportStoneRequest request,
            int stoneItemId,
            CargoTransportStoneResult result,
            out UsableCountLimitState usableCountState)
        {
            usableCountState = null;
            if (!UsableCountLimitService.TryRecordUseIfLimited(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    stoneItemId,
                    1,
                    out usableCountState))
            {
                return Fail(result, CargoTransportStoneStatus.UsableCountLimitExceeded, "usable count limit exceeded", false);
            }

            if (!InventoryDeleteService.TryUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    request.StoneSlotIndex,
                    stoneItemId,
                    out var stoneMutation))
            {
                result.Status = CargoTransportStoneStatus.MutationFailed;
                result.Detail = "stone consume failed";
                return false;
            }

            result.StoneMutation = stoneMutation;
            result.AckRemainingStoneCount = stoneMutation.RemainingStackCount;
            result.StoneMutation.UsableCountState = usableCountState;
            AddUnique(result.MainRefreshSlots, request.StoneSlotIndex);
            return true;
        }

        private static bool TransferEquipmentToAccountCargo(
            InventoryService inventory,
            CargoTransportStoneRequest request,
            ItemCore equipment,
            short cargoSlot,
            CargoTransportStoneResult result)
        {
            var transfer = equipment.Copy();
            transfer.SortLockFlag = 0;
            transfer.EquipmentLockId = 0;

            if (!inventory.RemoveItem(InventoryListType.Main, request.TargetSlotIndex))
            {
                result.Status = CargoTransportStoneStatus.MutationFailed;
                result.Detail = "target equipment remove failed";
                return false;
            }

            if (!inventory.SetItem(InventoryListType.AccountCargo, cargoSlot, transfer))
            {
                result.Status = CargoTransportStoneStatus.MutationFailed;
                result.Detail = "account cargo set failed";
                return false;
            }

            result.TargetMutation = BuildRemovedMutation(
                InventoryListType.Main,
                request.TargetSlotIndex,
                equipment);
            AddUnique(result.MainRefreshSlots, request.TargetSlotIndex);
            AddUnique(result.AccountCargoRefreshSlots, cargoSlot);
            return true;
        }

        private static bool TransferCreatureByMail(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            CargoTransportStoneRequest request,
            CharacterRecord targetCharacter,
            ItemCore creature,
            MailboxService mailboxService,
            CargoTransportStoneResult result)
        {
            if (mailboxService == null)
            {
                result.Status = CargoTransportStoneStatus.MailFailed;
                result.Detail = "mailbox service is unavailable";
                return false;
            }

            var attachment = BuildCreatureAttachment(
                inventory,
                creature,
                request.TargetSlotIndex);
            var mailRequest = new MailboxSendRequest
            {
                SenderCharacterId = inventory.CharacterId,
                SenderAccountId = inventory.AccountId,
                SenderName = string.Empty,
                ReceiverCharacterId = targetCharacter.CharacterId,
                ReceiverAccountId = targetCharacter.AccountId,
                ReceiverName = targetCharacter.DisplayName,
                Gold = 0,
                Title = "Cargo transport stone",
                Text = "Cargo transport stone",
                MailType = 1,
                SourceProtocol = SourceProtocol,
                Unlimited = true,
                AuditActor = "cargo-transport-stone",
                AuditReason = "creature transport stone",
                IdempotencyKey = $"cargo-transport-stone:{inventory.CharacterId}:{Guid.NewGuid():N}",
                Attachments = new[] { attachment },
            };

            if (!inventory.RemoveItem(InventoryListType.Pet, request.TargetSlotIndex))
            {
                result.Status = CargoTransportStoneStatus.MutationFailed;
                result.Detail = "target creature remove failed";
                return false;
            }

            if (creature.CreatureUid > 0)
            {
                CreatureDetailRepository.Delete(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    creature.CreatureUid);
                inventory.CreatureDetails.Detach(creature.CreatureUid);
            }

            var mailResult = mailboxService.SendSystemMails(
                connection,
                transaction,
                new[] { mailRequest });
            if (!mailResult.Success)
            {
                result.Status = CargoTransportStoneStatus.MailFailed;
                result.Detail = $"mail send failed: {mailResult.Error}";
                return false;
            }

            result.TargetMutation = BuildRemovedMutation(
                InventoryListType.Pet,
                request.TargetSlotIndex,
                creature);
            AddUnique(result.PetRefreshSlots, request.TargetSlotIndex);
            result.CreatureListChanged = true;
            return true;
        }

        private static MailboxSendAttachmentRequest BuildCreatureAttachment(
            InventoryService inventory,
            ItemCore creature,
            short sourceSlotIndex)
        {
            var core = creature.Copy();
            core.SortLockFlag = 0;
            core.EquipmentLockId = 0;
            core.CreatureUid = 0;

            return new MailboxSendAttachmentRequest
            {
                ItemType = 3,
                ItemSlot = (ushort)Math.Max(0, (int)sourceSlotIndex),
                ItemId = core.ItemId,
                ItemCount = 1,
                InstanceValue = core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = core.AbilityNo,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid,
                ExtraJson = "{}",
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = MailboxItemDetailCodec.Capture(inventory, creature),
            };
        }

        private static bool TryFindAccountCargoSlot(
            InventoryService inventory,
            out short slotIndex)
        {
            slotIndex = -1;
            if (inventory == null)
                return false;

            var range = ItemSlotBoundService.GetAccountCargoOpenRange(
                inventory.GetListParam16(InventoryListType.AccountCargo));
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                var candidate = (short)slot;
                if (inventory.AccountCargo.IsOpenSlot(candidate)
                    && inventory.GetItem(InventoryListType.AccountCargo, candidate) == null)
                {
                    slotIndex = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool AllowsRarity(StackableItemFile stackable, int rarity)
        {
            var restrictions = stackable?.RarityPossibleExplain;
            if (restrictions == null || restrictions.Count == 0)
                return true;

            return rarity >= 0
                && rarity < restrictions.Count
                && restrictions[rarity] != 0;
        }

        private static string NormalizeActionType(string raw)
        {
            var value = (raw ?? string.Empty).Trim().Trim('`').Trim();
            if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
                value = value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        private static InventoryMutationResult BuildRemovedMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore source)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source?.ItemId ?? 0,
                RemainingStackCount = 0,
                InstanceValue = source != null
                    ? source.InstanceValue
                    : 0,
                Durability = source != null ? source.Durability : (ushort)0,
                RequestedCount = 1,
                AppliedCount = 1,
            };
        }

        private static bool Fail(
            CargoTransportStoneResult result,
            CargoTransportStoneStatus status,
            string detail,
            bool returnValue)
        {
            result.Status = status;
            result.Detail = detail;
            return returnValue;
        }

        private static void AddUnique(ICollection<short> slots, short slotIndex)
        {
            if (slots == null || slotIndex < 0 || slots.Contains(slotIndex))
                return;

            slots.Add(slotIndex);
        }
    }
}
