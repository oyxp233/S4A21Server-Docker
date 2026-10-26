using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PvfLib
{
    public sealed record GuildDungeonRewardDefinition(int MaximumRatio, int ItemId, int Count, int Contribution);
    public sealed record GuildDungeonDefinition(int Id, int GaugeLimit, int GaugePerClear,
        int ConquestRatio, IReadOnlyList<int> Prerequisites, IReadOnlyList<GuildDungeonRewardDefinition> Rewards)
    {
        // A21 02A4A670 multiplies the received clear COUNT by [gauge on clear].
        public int Ratio(int clears) => (int)Math.Min(100, Math.Max(0, (long)clears * GaugePerClear * 100 / GaugeLimit));
    }

    public sealed class GuildDungeonFile
    {
        public IReadOnlyList<GuildDungeonDefinition> Dungeons { get; }
        public GuildDungeonFile(string text)
        {
            var root = new ScriptParser().Parse(text);
            int[] Numbers(ScriptNode node)
            {
                if (node == null) return Array.Empty<int>();
                return string.Join(" ", node.DataItems.Select(i => i.GetContent(text)))
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
            }
            int One(ScriptNode parent, string tag)
            {
                var values = Numbers(parent.GetChild(tag));
                if (values.Length != 1) throw new FormatException($"Guild dungeon [{tag}] must contain one integer.");
                return values[0];
            }
            var definitions = new List<GuildDungeonDefinition>();
            foreach (var node in root.GetChildren("dungeon"))
            {
                var rewards = new List<GuildDungeonRewardDefinition>();
                foreach (var reward in node.GetChild("reward")?.GetChildren("gauge ratio")
                    ?? throw new FormatException("Guild dungeon rewards missing."))
                {
                    var ratio = Numbers(reward);
                    var item = Numbers(reward.GetChild("item"));
                    int contribution = One(reward, "contribute");
                    if (ratio.Length != 1 || ratio[0] < 0 || ratio[0] > 100 || item.Length != 2
                        || item[0] <= 0 || item[1] <= 0 || contribution < 0)
                        throw new FormatException("Invalid guild dungeon reward.");
                    rewards.Add(new(ratio[0], item[0], item[1], contribution));
                }
                var d = new GuildDungeonDefinition(One(node, "dungeon index"), One(node, "gauge limit"),
                    One(node, "gauge on clear"), One(node, "conquest gauge ratio"),
                    Array.AsReadOnly(Numbers(node.GetChild("required dungeon conquest"))),
                    rewards.OrderBy(r => r.MaximumRatio).ToList().AsReadOnly());
                if (d.Id <= 0 || d.GaugeLimit <= 0 || d.GaugePerClear <= 0 || d.ConquestRatio < 0
                    || d.ConquestRatio > 100 || d.Prerequisites.Any(p => p <= 0 || p == d.Id)
                    || d.Rewards.Count == 0 || d.Rewards[^1].MaximumRatio != 100
                    || d.Rewards.Select(r => r.MaximumRatio).Distinct().Count() != d.Rewards.Count)
                    throw new FormatException("Invalid guild dungeon definition.");
                definitions.Add(d);
            }
            if (definitions.Count == 0 || definitions.Select(d => d.Id).Distinct().Count() != definitions.Count
                || definitions.Any(d => d.Prerequisites.Any(p => definitions.All(other => other.Id != p))))
                throw new FormatException("Guild dungeon list missing, duplicated or references unknown prerequisites.");
            var visited = new HashSet<int>();
            var visiting = new HashSet<int>();
            void Visit(int id)
            {
                if (visited.Contains(id)) return;
                if (!visiting.Add(id)) throw new FormatException("Guild dungeon prerequisite cycle.");
                foreach (var p in definitions.Single(d => d.Id == id).Prerequisites) Visit(p);
                visiting.Remove(id); visited.Add(id);
            }
            foreach (var d in definitions) Visit(d.Id);
            Dungeons = definitions.AsReadOnly();
        }
    }
}
