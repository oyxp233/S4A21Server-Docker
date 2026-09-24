using System;
using System.Collections.Generic;
using System.Linq;

namespace PvfLib
{
    public enum RaidEtcTriggerKind
    {
        Unknown,
        PhaseInitialization,
        DungeonOpened,
        DungeonCleared,
        TimerEnded,
    }

    public sealed class RaidEtcTriggerContext
    {
        public RaidEtcTriggerKind Kind { get; init; }
        public int? DungeonId { get; init; }
        public int? TimerType { get; init; }
        public string State { get; init; } = string.Empty;
        public IReadOnlyList<string> NormalizedClauses { get; init; } = Array.Empty<string>();
    }

    public sealed class RaidTimerDirective
    {
        public int PhaseIndex { get; init; }
        public int TimerType { get; init; }
        public int DungeonId { get; init; }
        public int Seconds { get; init; }
        public int SourceOrder { get; init; }
        public int SourceLineIndex { get; init; }
        public RaidEtcTriggerContext Trigger { get; init; } = new RaidEtcTriggerContext();
    }

    public sealed class RaidReservedDungeonStateDirective
    {
        public int PhaseIndex { get; init; }
        public int DungeonId { get; init; }
        public string State { get; init; } = string.Empty;
        public int Seconds { get; init; }
        public int SourceOrder { get; init; }
        public int SourceLineIndex { get; init; }
        public RaidEtcTriggerContext Trigger { get; init; } = new RaidEtcTriggerContext();
    }

    public sealed class RaidRankCondition
    {
        public int MinimumDeathCount { get; init; }
        public int MaximumDeathCount { get; init; }
        public int Rank { get; init; }
    }

    public sealed class RaidStateReward
    {
        public string RewardType { get; init; } = string.Empty;
        public int State { get; init; }
        public int Weight { get; init; }
        public int ItemId { get; init; }
        public int Flags { get; init; }
    }

    public sealed class RaidEtcPhase
    {
        public List<RaidRankCondition> RankConditions { get; } = new List<RaidRankCondition>();
        public List<RaidStateReward> StateRewards { get; } = new List<RaidStateReward>();
        public List<RaidTimerDirective> TimerDirectives { get; } = new List<RaidTimerDirective>();
        public List<RaidReservedDungeonStateDirective> ReservedDungeonStates { get; } = new List<RaidReservedDungeonStateDirective>();

        public int ResolveRank(int deathCount)
        {
            foreach (var condition in RankConditions.OrderBy(entry => entry.MinimumDeathCount))
            {
                if (deathCount >= condition.MinimumDeathCount
                    && deathCount <= condition.MaximumDeathCount)
                    return condition.Rank;
            }

            if (RankConditions.Count == 0)
                return -1;
            if (deathCount < RankConditions.Min(entry => entry.MinimumDeathCount))
                return RankConditions.OrderBy(entry => entry.MinimumDeathCount).First().Rank;

            return RankConditions.OrderByDescending(entry => entry.MaximumDeathCount).First().Rank;
        }

        public int GetRewardWeight(string rewardType, int state)
        {
            return GetRewardCandidates(rewardType, state).Sum(entry => Math.Max(0, entry.Weight));
        }

        public bool TrySelectReward(string rewardType, int state, int roll, out RaidStateReward reward)
        {
            reward = null;
            var candidates = GetRewardCandidates(rewardType, state);
            var totalWeight = candidates.Sum(entry => Math.Max(0, entry.Weight));
            if (roll < 0 || roll >= totalWeight)
                return false;

            foreach (var candidate in candidates)
            {
                roll -= Math.Max(0, candidate.Weight);
                if (roll >= 0)
                    continue;

                reward = candidate;
                return true;
            }

            return false;
        }

        private List<RaidStateReward> GetRewardCandidates(string rewardType, int state)
        {
            var typed = StateRewards
                .Where(entry => string.Equals(
                    entry.RewardType,
                    rewardType,
                    StringComparison.OrdinalIgnoreCase)
                    && entry.Weight > 0)
                .ToList();
            var exact = typed.Where(entry => entry.State == state).ToList();
            return exact.Count > 0
                ? exact
                : typed.Where(entry => entry.State == -1).ToList();
        }
    }

    public sealed class RaidEtcFile : PvfModelBase
    {
        public int StartDelaySeconds { get; private set; }
        public int PhaseBreakSeconds { get; private set; }
        public List<int> ShieldChargeRates { get; } = new List<int>();
        public int HatcheryTotalCount { get; private set; }
        public int HatcheryOpenCount { get; private set; }
        public List<int> HatcheryDungeonIds { get; } = new List<int>();
        public List<int> ExceptCheatDungeonIds { get; } = new List<int>();
        public List<int> PhaseTimeOverSeconds { get; } = new List<int>();
        public List<string> ParseWarnings { get; } = new List<string>();
        public List<RaidEtcPhase> Phases { get; } = new List<RaidEtcPhase>();
        public List<RaidRankCondition> RankConditions { get; } = new List<RaidRankCondition>();
        public List<RaidStateReward> StateRewards { get; } = new List<RaidStateReward>();

        public static RaidEtcFile Parse(string content)
        {
            var root = string.IsNullOrEmpty(content)
                ? new ScriptNode { Tag = "ROOT" }
                : new ScriptParser().Parse(content);
            var file = new RaidEtcFile
            {
                Root = root,
                Content = content ?? string.Empty,
            };

            var startDelay = ParseRootValues(root, "start delay time", file.Content);
            if (startDelay.Length > 0)
                file.StartDelaySeconds = startDelay[0];

            var phaseBreakTime = ParseRootValues(root, "phase break time", file.Content);
            if (phaseBreakTime.Length > 0)
                file.PhaseBreakSeconds = phaseBreakTime[0];

            file.PhaseTimeOverSeconds.AddRange(
                ParsePhaseTimeOverValues(root, file.Content, file.ParseWarnings));

            file.ShieldChargeRates.AddRange(
                ParseRootValues(root, "shield charge rate", file.Content));

            var hatcheryInfo = ParseRootValues(root, "hatchery info", file.Content);
            if (hatcheryInfo.Length >= 2)
            {
                file.HatcheryTotalCount = hatcheryInfo[0];
                file.HatcheryOpenCount = hatcheryInfo[1];
                file.HatcheryDungeonIds.AddRange(hatcheryInfo.Skip(2));
            }

            file.ExceptCheatDungeonIds.AddRange(
                ParseRootValues(root, "except cheat dungeon", file.Content));

            var phaseNodes = root.GetChildren("phase");
            if (phaseNodes.Count == 0)
            {
                file.Phases.Add(ParsePhase(root, file.Content, 0, file.ParseWarnings));
            }
            else
            {
                for (var phaseIndex = 0; phaseIndex < phaseNodes.Count; phaseIndex++)
                    file.Phases.Add(ParsePhase(
                        phaseNodes[phaseIndex],
                        file.Content,
                        phaseIndex,
                        file.ParseWarnings));
            }

            if (file.Phases.Count > 0)
            {
                file.RankConditions.AddRange(file.Phases[0].RankConditions);
                file.StateRewards.AddRange(file.Phases[0].StateRewards);
            }

            return file;
        }

        public RaidEtcPhase GetPhase(int phaseIndex)
        {
            return phaseIndex >= 0 && phaseIndex < Phases.Count ? Phases[phaseIndex] : null;
        }

        public int ResolveRank(int deathCount)
        {
            return GetPhase(0)?.ResolveRank(deathCount) ?? -1;
        }

        public int GetRewardWeight(string rewardType, int state)
        {
            return GetPhase(0)?.GetRewardWeight(rewardType, state) ?? 0;
        }

        public bool TrySelectReward(string rewardType, int state, int roll, out RaidStateReward reward)
        {
            var phase = GetPhase(0);
            if (phase != null)
                return phase.TrySelectReward(rewardType, state, roll, out reward);
            reward = null;
            return false;
        }

        private static RaidEtcPhase ParsePhase(
            ScriptNode phaseNode,
            string content,
            int phaseIndex,
            List<string> warnings)
        {
            var phase = new RaidEtcPhase();
            var rankNodes = new List<ScriptNode>();
            var rankNode = phaseNode.GetChild("condition for rank");
            if (rankNode != null)
                rankNodes.AddRange(rankNode.GetChildren("dead count"));
            rankNodes.AddRange(phaseNode.GetChildren("dead count"));
            foreach (var node in rankNodes.GroupBy(entry => entry.StartIndex).Select(group => group.First()))
            {
                var values = ParseIntArray(node.GetFirstDataContent(content));
                if (values == null || values.Length < 3)
                    continue;

                phase.RankConditions.Add(new RaidRankCondition
                {
                    MinimumDeathCount = values[0],
                    MaximumDeathCount = values[1],
                    Rank = values[2],
                });
            }

            var rewardNodes = new List<ScriptNode>();
            var rewardNode = phaseNode.GetChild("clear reward item");
            if (rewardNode != null)
                rewardNodes.AddRange(rewardNode.GetChildren("state reward"));
            rewardNodes.AddRange(phaseNode.GetChildren("state reward"));
            foreach (var node in rewardNodes.GroupBy(entry => entry.StartIndex).Select(group => group.First()))
            {
                var tokens = SplitTokens(node.GetFirstDataContent(content));
                if (tokens.Length < 5
                    || !int.TryParse(tokens[1], out var state)
                    || !int.TryParse(tokens[2], out var weight)
                    || !int.TryParse(tokens[3], out var itemId)
                    || !int.TryParse(tokens[4], out var flags))
                    continue;

                phase.StateRewards.Add(new RaidStateReward
                {
                    RewardType = StripBacktick(tokens[0]),
                    State = state,
                    Weight = weight,
                    ItemId = itemId,
                    Flags = flags,
                });
            }

            RaidEtcTriggerContext currentTrigger = null;
            var sourceOrder = 0;
            foreach (var child in phaseNode.Children.OrderBy(entry => entry.StartIndex))
            {
                if (string.Equals(child.Tag, "trigger", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrigger = ParseTrigger(child, content, phaseIndex, warnings);
                    continue;
                }

                if (!string.Equals(child.Tag, "behavior", StringComparison.OrdinalIgnoreCase))
                    continue;

                var trigger = currentTrigger ?? new RaidEtcTriggerContext();
                foreach (var directive in EnumerateDescendants(child)
                    .Where(entry => IsTag(entry, "set timer") || IsTag(entry, "reserve dungeon state"))
                    .OrderBy(entry => entry.StartIndex))
                {
                    var order = sourceOrder++;
                    if (IsTag(directive, "set timer"))
                    {
                        var tokens = SplitTokens(directive.GetFirstDataContent(content));
                        if (tokens.Length != 3
                            || !int.TryParse(tokens[0], out var timerType)
                            || !int.TryParse(tokens[1], out var dungeonId)
                            || !int.TryParse(tokens[2], out var seconds))
                        {
                            AddParseWarning(warnings, phaseIndex, directive, "SET TIMER", "expected three integer values");
                            continue;
                        }

                        phase.TimerDirectives.Add(new RaidTimerDirective
                        {
                            PhaseIndex = phaseIndex,
                            TimerType = timerType,
                            DungeonId = dungeonId,
                            Seconds = seconds,
                            SourceOrder = order,
                            SourceLineIndex = directive.StartLineIndex,
                            Trigger = trigger,
                        });
                        continue;
                    }

                    var reserveTokens = SplitTokens(directive.GetFirstDataContent(content));
                    if (reserveTokens.Length != 3
                        || !int.TryParse(reserveTokens[0], out var reserveDungeonId)
                        || !int.TryParse(reserveTokens[2], out var reserveSeconds))
                    {
                        AddParseWarning(warnings, phaseIndex, directive, "RESERVE DUNGEON STATE", "expected dungeon, state and integer seconds");
                        continue;
                    }

                    var state = (StripBacktick(reserveTokens[1]) ?? string.Empty).Trim().ToLowerInvariant();
                    if (!string.Equals(state, "open", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(state, "clear", StringComparison.OrdinalIgnoreCase))
                    {
                        AddParseWarning(warnings, phaseIndex, directive, "RESERVE DUNGEON STATE", $"unsupported state '{state}'");
                        continue;
                    }

                    phase.ReservedDungeonStates.Add(new RaidReservedDungeonStateDirective
                    {
                        PhaseIndex = phaseIndex,
                        DungeonId = reserveDungeonId,
                        State = state,
                        Seconds = reserveSeconds,
                        SourceOrder = order,
                        SourceLineIndex = directive.StartLineIndex,
                        Trigger = trigger,
                    });
                }
            }

            return phase;
        }

        private static RaidEtcTriggerContext ParseTrigger(
            ScriptNode triggerNode,
            string content,
            int phaseIndex,
            List<string> warnings)
        {
            var clauses = EnumerateDescendants(triggerNode)
                .OrderBy(entry => entry.StartIndex)
                .ToArray();
            var normalizedClauses = clauses
                .Select(entry => $"[{(entry.Tag ?? string.Empty).Trim().ToUpperInvariant()}] {entry.GetFirstDataContent(content).Trim()}".TrimEnd())
                .ToArray();

            foreach (var clause in clauses.Where(entry => IsTag(entry, "check timer end")))
            {
                var tokens = SplitTokens(clause.GetFirstDataContent(content));
                if (tokens.Length == 2
                    && int.TryParse(tokens[0], out var timerType)
                    && int.TryParse(tokens[1], out var dungeonId))
                {
                    return new RaidEtcTriggerContext
                    {
                        Kind = RaidEtcTriggerKind.TimerEnded,
                        DungeonId = dungeonId,
                        TimerType = timerType,
                        NormalizedClauses = normalizedClauses,
                    };
                }

                AddParseWarning(warnings, phaseIndex, clause, "CHECK TIMER END", "expected timer type and dungeon id");
            }

            foreach (var clause in clauses.Where(entry => IsTag(entry, "check dungeon state")))
            {
                var tokens = SplitTokens(clause.GetFirstDataContent(content));
                if (tokens.Length != 2 || !int.TryParse(tokens[0], out var dungeonId))
                {
                    AddParseWarning(warnings, phaseIndex, clause, "CHECK DUNGEON STATE", "expected dungeon id and state");
                    continue;
                }

                var state = (StripBacktick(tokens[1]) ?? string.Empty).Trim().ToLowerInvariant();
                var kind = string.Equals(state, "open", StringComparison.OrdinalIgnoreCase)
                    ? RaidEtcTriggerKind.DungeonOpened
                    : string.Equals(state, "clear", StringComparison.OrdinalIgnoreCase)
                        ? RaidEtcTriggerKind.DungeonCleared
                        : RaidEtcTriggerKind.Unknown;
                if (kind == RaidEtcTriggerKind.Unknown
                    && !string.Equals(state, "hide", StringComparison.OrdinalIgnoreCase))
                    AddParseWarning(warnings, phaseIndex, clause, "CHECK DUNGEON STATE", $"unsupported state '{state}'");

                return new RaidEtcTriggerContext
                {
                    Kind = kind,
                    DungeonId = dungeonId,
                    State = state,
                    NormalizedClauses = normalizedClauses,
                };
            }

            if (clauses.Any(entry => IsTag(entry, "1phase init") || IsTag(entry, "2phase init")))
            {
                return new RaidEtcTriggerContext
                {
                    Kind = RaidEtcTriggerKind.PhaseInitialization,
                    NormalizedClauses = normalizedClauses,
                };
            }

            return new RaidEtcTriggerContext
            {
                Kind = RaidEtcTriggerKind.Unknown,
                NormalizedClauses = normalizedClauses,
            };
        }

        private static IEnumerable<ScriptNode> EnumerateDescendants(ScriptNode node)
        {
            foreach (var child in node.Children.OrderBy(entry => entry.StartIndex))
            {
                yield return child;
                foreach (var descendant in EnumerateDescendants(child))
                    yield return descendant;
            }
        }

        private static bool IsTag(ScriptNode node, string tag)
        {
            return string.Equals(node?.Tag, tag, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddParseWarning(
            List<string> warnings,
            int phaseIndex,
            ScriptNode node,
            string tag,
            string detail)
        {
            warnings.Add($"Anton phase {phaseIndex + 1} [{tag}] line {node.StartLineIndex + 1}: {detail}.");
        }

        private static string[] SplitTokens(string data)
        {
            return (data ?? string.Empty).Split(
                new[] { ' ', (char)9, (char)10, (char)13 },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static int[] ParseRootValues(ScriptNode root, string tag, string content)
        {
            var node = root?.GetChild(tag);
            return node == null
                ? Array.Empty<int>()
                : ParseIntArray(node.GetFirstDataContent(content)) ?? Array.Empty<int>();
        }

        private static int[] ParsePhaseTimeOverValues(ScriptNode root, string content, List<string> warnings)
        {
            var node = root?.GetChild("phase time over");
            if (node == null)
                return Array.Empty<int>();

            var tokens = SplitTokens(node.GetFirstDataContent(content));
            var values = new int[tokens.Length];
            for (var index = 0; index < tokens.Length; index++)
            {
                if (!int.TryParse(tokens[index], out values[index]))
                {
                    warnings.Add($"Anton [PHASE TIME OVER] line {node.StartLineIndex + 1}: invalid phase {index + 1} limit '{tokens[index]}'.");
                }
            }
            return values;
        }
    }
}
