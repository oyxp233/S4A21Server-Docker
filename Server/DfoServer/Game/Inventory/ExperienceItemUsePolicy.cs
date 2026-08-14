using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;

namespace DfoServer.Game.Inventory
{
    internal enum ExperienceItemUseLocation
    {
        Town,
        Dungeon,
    }

    internal enum ExperienceItemUseStatus
    {
        NotApplicable,
        Success,
        InvalidOwner,
        UnsupportedDefinition,
        Expired,
        JobRestricted,
        LocationRestricted,
        LevelRestricted,
        NoExperienceGain,
        CooldownActive,
        ConsumeFailed,
        PersistenceFailed,
    }

    internal sealed class ExperienceItemUseResult
    {
        internal ExperienceItemUseStatus Status { get; set; }
        internal int AccountId { get; set; }
        internal int ItemTemplateId { get; set; }
        internal InventoryMutationResult ConsumedItem { get; set; }
        internal byte PreviousLevel { get; set; }
        internal byte NewLevel { get; set; }
        internal uint PreviousExp { get; set; }
        internal uint NewExp { get; set; }
        internal uint GrantedExp { get; set; }
        internal uint HonorExpGain { get; set; }
        internal ulong TotalHonorExp { get; set; }
        internal uint TotalGrowthCapsuleExp { get; set; }
        internal SkillInfoSnapshot SyncedSkills { get; set; }
        internal SkillPointProtocolState SkillPoints { get; set; }
        internal string Detail { get; set; }

        internal bool Success => Status == ExperienceItemUseStatus.Success;
    }

    internal sealed class ExperienceItemUseContext
    {
        internal ExperienceItemDefinition Definition { get; set; }
        internal int SourceExpireTime { get; set; }
        internal uint NowUnixTime { get; set; }
        internal byte Job { get; set; }
        internal byte Level { get; set; }
        internal uint Exp { get; set; }
        internal bool IsHardcore { get; set; }
        internal ExperienceItemUseLocation Location { get; set; }
    }

    internal sealed class ExperienceItemUsePlan
    {
        internal ExperienceItemUseStatus Status { get; set; }
        internal string Detail { get; set; }
        internal uint GrantedExp { get; set; }
        internal uint HonorExpGain { get; set; }
        internal uint NewExp { get; set; }
        internal byte NewLevel { get; set; }

        internal bool Success => Status == ExperienceItemUseStatus.Success;
    }

    internal static class ExperienceItemUsePolicy
    {
        internal static ExperienceItemUsePlan Evaluate(ExperienceItemUseContext context)
        {
            if (context?.Definition == null || !context.Definition.IsSupported)
            {
                return Reject(
                    ExperienceItemUseStatus.UnsupportedDefinition,
                    context?.Definition?.UnsupportedReason
                        ?? "experience definition is unavailable");
            }

            var definition = context.Definition;
            if ((context.SourceExpireTime > 0
                 && (uint)context.SourceExpireTime <= context.NowUnixTime)
                || !definition.IsTemplateAvailableAt(context.NowUnixTime))
                return Reject(ExperienceItemUseStatus.Expired, "item has expired");

            if (definition.UsablePeriodDays > 0 && context.SourceExpireTime <= 0)
                return Reject(
                    ExperienceItemUseStatus.Expired,
                    "timed item has no instance expiration");

            if (!definition.IsUsableByJob(context.Job))
                return Reject(
                    ExperienceItemUseStatus.JobRestricted,
                    $"job={context.Job} is not permitted");

            if (definition.TownOnly && context.Location != ExperienceItemUseLocation.Town)
                return Reject(
                    ExperienceItemUseStatus.LocationRestricted,
                    "item can only be used in town");

            if (definition.BlockedInHardcore && context.IsHardcore)
                return Reject(
                    ExperienceItemUseStatus.LevelRestricted,
                    "item is disabled in hardcore mode");

            if ((definition.MinimumLevel >= 0 && context.Level < definition.MinimumLevel)
                || (definition.MaximumLevel >= 0 && context.Level > definition.MaximumLevel)
                || context.Level >= ExpTableProvider.MaxLevel)
                return Reject(
                    ExperienceItemUseStatus.LevelRestricted,
                    $"level={context.Level} allowed={definition.MinimumLevel}..{definition.MaximumLevel}");

            var grantedExp = definition.CalculateGain(context.Level);
            if (grantedExp == 0)
                return Reject(
                    ExperienceItemUseStatus.NoExperienceGain,
                    "calculated experience is zero");

            // 拆分/累加/升级判定统一走经验系统的数学核, 此处只做预演不落库。
            var plan = Progression.CharacterExperienceService.Plan(
                context.Level, context.Exp, grantedExp);
            return new ExperienceItemUsePlan
            {
                Status = ExperienceItemUseStatus.Success,
                GrantedExp = grantedExp,
                HonorExpGain = plan.HonorExpGain,
                NewExp = plan.NewExp,
                NewLevel = plan.NewLevel,
            };
        }

        private static ExperienceItemUsePlan Reject(
            ExperienceItemUseStatus status,
            string detail)
            => new ExperienceItemUsePlan
            {
                Status = status,
                Detail = detail,
            };
    }
}
