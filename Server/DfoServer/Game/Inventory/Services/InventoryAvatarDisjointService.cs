using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal delegate bool TryResolveAvatarDisjointMaterials(
        ItemCore source,
        ItemMetadata metadata,
        out List<DisjointMaterialResult> materials);

    internal static class InventoryAvatarDisjointService
    {
        internal static bool TryDisjointAvatar(
            InventoryService inventory,
            AvatarDisjointRequest request,
            out AvatarDisjointResult result)
            => TryDisjointAvatar(
                inventory,
                request,
                TryResolveSystemMaterials,
                out result);

        internal static bool TryDisjointAvatar(
            InventoryService inventory,
            AvatarDisjointRequest request,
            TryResolveAvatarDisjointMaterials tryResolveMaterials,
            out AvatarDisjointResult result)
        {
            result = CreateErrorResult(request, AvatarDisjointResult.ErrorInvalidRequest);
            if (inventory == null
                || request == null
                || tryResolveMaterials == null
                || request.SlotIndex < 0)
                return false;

            var source = inventory.GetItem(InventoryListType.Avatar, request.SlotIndex);
            if (source == null || source.ItemKind != ItemCore.KindAvatar)
                return false;
            if (request.ExpectedItemTemplateId > 0 && request.ExpectedItemTemplateId != source.ItemId)
                return false;
            if (IsItemLocked(inventory, source))
                return false;

            var metadata = ItemMetadataResolver.Resolve(source.ItemId);
            if (!TryValidateAvatarDisjoint(source, metadata))
                return false;

            if (!tryResolveMaterials(source, metadata, out var materials)
                || materials == null
                || materials.Count == 0)
            {
                FileLogger.Log($"[AvatarDisjoint] no PVF reward pool item=0x{source.ItemId:X8} grade={metadata.Grade}");
                return false;
            }

            if (!CanGrantMaterials(inventory, materials))
            {
                result = CreateErrorResult(request, AvatarDisjointResult.ErrorInventoryFull);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (!InventoryDeleteService.TryRemoveSlot(
                    inventory,
                    InventoryListType.Avatar,
                    request.SlotIndex,
                    out _))
            {
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (!GrantMaterials(inventory, materials))
            {
                result = CreateErrorResult(request, AvatarDisjointResult.ErrorInventoryFull);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            result = new AvatarDisjointResult
            {
                Request = request,
                SourceItemTemplateId = source.ItemId,
                ErrorCode = 0,
            };
            result.Materials.AddRange(materials);
            return true;
        }

        private static bool TryResolveSystemMaterials(
            ItemCore source,
            ItemMetadata metadata,
            out List<DisjointMaterialResult> materials)
        {
            materials = AvatarDisjointConfigProvider.Calculate(metadata?.Grade ?? -1);
            return materials.Count > 0;
        }

        private static bool TryValidateAvatarDisjoint(ItemCore source, ItemMetadata metadata)
        {
            if (source == null
                || metadata == null
                || source.ItemKind != ItemCore.KindAvatar
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(metadata.EquipmentType)
                || metadata.EquipmentType.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return !ContainsImpossibleContent(metadata, "disjoint");
        }

        private static bool CanGrantMaterials(
            InventoryService inventory,
            IReadOnlyList<DisjointMaterialResult> materials)
        {
            return BuildGrantRequests(materials, out var requests)
                && InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan)
                && plan.Success;
        }

        private static bool GrantMaterials(
            InventoryService inventory,
            IReadOnlyList<DisjointMaterialResult> materials)
        {
            if (!BuildGrantRequests(materials, out var requests)
                || !InventoryRewardGrantService.TryGrantBatch(inventory, requests, out var grantResult)
                || !grantResult.Success
                || grantResult.Results.Count != materials.Count)
                return false;

            for (var index = 0; index < materials.Count; index++)
            {
                var material = materials[index];
                var grant = grantResult.Results[index];
                material.SlotIndex = grant.SlotIndex;
            }

            return true;
        }

        private static bool BuildGrantRequests(
            IReadOnlyList<DisjointMaterialResult> materials,
            out List<InventoryRewardGrantRequest> requests)
        {
            requests = new List<InventoryRewardGrantRequest>();
            if (materials == null)
                return false;

            foreach (var material in materials)
            {
                if (material == null || material.ItemTemplateId <= 0 || material.Count <= 0)
                    return false;

                requests.Add(InventoryRewardGrantRequest.Create(
                    material.ItemTemplateId,
                    material.Count,
                    ItemCreateReason.Unknown));
            }

            return requests.Count > 0;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static bool ContainsImpossibleContent(ItemMetadata metadata, string expected)
        {
            if (metadata.ImpossibleContents == null)
                return false;

            foreach (var item in metadata.ImpossibleContents)
            {
                if (string.Equals(NormalizePvfToken(item), expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string NormalizePvfToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Trim('`').Trim();
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                normalized = normalized.Substring(1, normalized.Length - 2);

            return normalized.Trim();
        }

        private static AvatarDisjointResult CreateErrorResult(AvatarDisjointRequest request, byte errorCode)
        {
            return new AvatarDisjointResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }
    }
}
