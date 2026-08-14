using System.Collections.Generic;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    public sealed class ClearConditionState
    {
        private readonly object _sync = new object();
        private readonly List<ClearConditionEntry> _conditions;
        private readonly int[] _counters;
        private readonly Dictionary<int, int> _groupCounters = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _groupRequirements = new Dictionary<int, int>();
        public int TotalRequired { get; }
        public int CurrentProgress { get; private set; }

        public ClearConditionState(List<ClearConditionEntry> conditions)
        {
            _conditions = SnapshotConditions(conditions);
            _counters = new int[_conditions.Count];
            int total = 0;
            foreach (var c in _conditions)
            {
                if (c.GroupId > 0)
                {
                    var required = c.GroupRequired > 0 ? c.GroupRequired : 1;
                    if (!_groupRequirements.TryGetValue(c.GroupId, out var current)
                        || required > current)
                    {
                        _groupRequirements[c.GroupId] = required;
                    }
                    continue;
                }

                if (c.Count > 0)
                    total += c.Count;
            }

            foreach (var requirement in _groupRequirements.Values)
                total += requirement;

            TotalRequired = total;
        }

        /// <summary>同一份条件、计数器归零的新实例。组队进本 fan-out 时每成员各持一份 ——
        /// 引用共享会让多人并发 Check 互相污染计数(条件列表本身只读, 可共享)。</summary>
        public ClearConditionState CloneFresh()
        {
            return new ClearConditionState(_conditions);
        }

        // df_game_r CClearCondition::ClearCondition (0x82FEFCE)
        // 内置锁: 成员自己的 handler 线程与队友击杀 relay 线程都会调本方法, 计数器自增必须互斥。
        public bool Check(int type, int targetId)
        {
            lock (_sync)
            {
                for (int i = 0; i < _conditions.Count; i++)
                {
                    var c = _conditions[i];
                    if (c.Type == type && c.TargetId == targetId)
                    {
                        if (c.GroupId > 0)
                        {
                            // A clear-map group counts distinct candidate maps. Replaying
                            // the same room-clear event must not satisfy another member.
                            if (_counters[i] > 0)
                                continue;

                            _counters[i] = 1;
                            var required = _groupRequirements.TryGetValue(
                                c.GroupId,
                                out var groupRequired)
                                ? groupRequired
                                : 1;
                            _groupCounters.TryGetValue(c.GroupId, out var groupCount);
                            if (groupCount < required)
                            {
                                _groupCounters[c.GroupId] = groupCount + 1;
                                CurrentProgress++;
                            }
                            continue;
                        }

                        if (_counters[i] < c.Count)
                        {
                            _counters[i]++;
                            CurrentProgress++;
                        }
                    }
                }
                return TotalRequired > 0 && TotalRequired <= CurrentProgress;
            }
        }

        public bool IsCleared
        {
            get { lock (_sync) { return TotalRequired > 0 && TotalRequired <= CurrentProgress; } }
        }

        public bool HasConditions => TotalRequired > 0;

        private static List<ClearConditionEntry> SnapshotConditions(
            IReadOnlyList<ClearConditionEntry> conditions)
        {
            var snapshot = new List<ClearConditionEntry>(conditions?.Count ?? 0);
            if (conditions == null)
                return snapshot;

            foreach (var condition in conditions)
            {
                if (condition == null)
                    continue;
                snapshot.Add(new ClearConditionEntry
                {
                    Type = condition.Type,
                    TargetId = condition.TargetId,
                    Count = condition.Count,
                    GroupId = condition.GroupId,
                    GroupRequired = condition.GroupRequired,
                });
            }
            return snapshot;
        }
    }
}
