using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal readonly struct DungeonAdmissionRejectPresentation
    {
        internal DungeonAdmissionRejectPresentation(
            DungeonAdmissionRejectProjection projection,
            string noticeMessage)
        {
            Projection = projection;
            NoticeMessage = noticeMessage ?? string.Empty;
        }

        internal DungeonAdmissionRejectProjection Projection { get; }
        internal string NoticeMessage { get; }
        internal bool HasNotice => !string.IsNullOrEmpty(NoticeMessage);
    }

    internal static class SequentialDungeonAdmissionRejectPolicy
    {
        private static readonly DungeonAdmissionRejectPresentation
            NativePresentation = new DungeonAdmissionRejectPresentation(
                DungeonAdmissionRejectProjection.Native,
                string.Empty);

        internal static DungeonAdmissionRejectPresentation Resolve(
            EntryCostResult result,
            string memberName,
            byte memberSlot,
            SequentialDungeonDefinitionCatalog catalog,
            string targetDungeonName,
            IReadOnlyList<string> missingPrerequisiteDungeonNames)
        {
            if (result?.FailureKind
                    != EntryCostFailureKind.MissingPrerequisite
                || catalog == null
                || result.TargetDungeonId <= 0
                || result.MissingPrerequisiteDungeonIds.Count == 0)
            {
                return NativePresentation;
            }

            if (catalog.ResolvePrimaryByDungeonId(
                    result.TargetDungeonId,
                    out _) != SequentialDungeonCapabilityResolution.Resolved)
            {
                return NativePresentation;
            }

            if (catalog.ResolveRewardableByDungeonId(
                    result.TargetDungeonId,
                    out _) != SequentialDungeonCapabilityResolution.Absent)
            {
                return NativePresentation;
            }

            var displayName = string.IsNullOrWhiteSpace(memberName)
                ? $"槽位{memberSlot}"
                : memberName.Trim();
            var completeNames =
                !string.IsNullOrWhiteSpace(targetDungeonName)
                && missingPrerequisiteDungeonNames != null
                && missingPrerequisiteDungeonNames.Count
                    == result.MissingPrerequisiteDungeonIds.Count
                && missingPrerequisiteDungeonNames.All(
                    name => !string.IsNullOrWhiteSpace(name));
            if (!completeNames)
            {
                return new DungeonAdmissionRejectPresentation(
                    DungeonAdmissionRejectProjection.Silent,
                    $"队员[{displayName}]未满足前置地下城条件，" +
                    "无法进入该地下城。");
            }

            var missing = string.Join(
                "、",
                missingPrerequisiteDungeonNames.Select(
                    name => $"【{name.Trim()}】"));
            return new DungeonAdmissionRejectPresentation(
                DungeonAdmissionRejectProjection.Silent,
                $"队员[{displayName}]未通关{missing}，" +
                $"无法进入【{targetDungeonName.Trim()}】。");
        }
    }
}
