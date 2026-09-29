using System.Collections.Generic;

namespace PvfLib
{
    public sealed class NpcFavorPointRule
    {
        public int ItemId { get; set; }
        public int MinimumPointGain { get; set; }
        public int MaximumPointGain { get; set; }
    }

    public sealed class NpcFavorDecayRule
    {
        public int FavorLevel { get; set; }
        public int InactiveDays { get; set; }
        public int PointLoss { get; set; }
    }

    public sealed class NpcFavorSystemFile : PvfModelBase
    {
        public int ConditionLevel { get; set; } = -1;
        public int SpecialGiftItemCount { get; set; } = -1;
        public int SpecialGiftActionLimit { get; set; } = -1;
        public int TalkRate { get; set; } = -1;
        public int AppearDungeonRate { get; set; } = -1;
        public List<NpcFavorPointRule> PointRules { get; } = new List<NpcFavorPointRule>();
        public List<NpcFavorDecayRule> DecayRules { get; } = new List<NpcFavorDecayRule>();

        public static NpcFavorSystemFile Parse(string content)
        {
            var root = new ScriptParser().Parse(content ?? string.Empty);
            var result = new NpcFavorSystemFile
            {
                Root = root,
                Content = content ?? string.Empty,
            };

            foreach (var node in root.Children)
            {
                switch (node.Tag.ToLowerInvariant())
                {
                    case "favor condition level":
                        result.ConditionLevel = PvfScriptValueReader.ReadFirstInteger(node, result.Content);
                        break;
                    case "favor gift item count":
                        result.SpecialGiftItemCount = PvfScriptValueReader.ReadFirstInteger(node, result.Content);
                        break;
                    case "favor gift limit":
                        result.SpecialGiftActionLimit = PvfScriptValueReader.ReadFirstInteger(node, result.Content);
                        break;
                    case "favor talk rate":
                        result.TalkRate = PvfScriptValueReader.ReadFirstInteger(node, result.Content);
                        break;
                    case "favor appear dungeon rate":
                        result.AppearDungeonRate = PvfScriptValueReader.ReadFirstInteger(node, result.Content);
                        break;
                    case "favor level point up":
                        ParsePointRules(node, result);
                        break;
                    case "favor level point down":
                        ParseDecayRules(node, result);
                        break;
                }
            }

            return result;
        }

        private static void ParsePointRules(ScriptNode node, NpcFavorSystemFile result)
        {
            var values = PvfScriptValueReader.ReadIntegers(node, result.Content);
            for (var index = 0; index + 2 < values.Count; index += 3)
            {
                result.PointRules.Add(new NpcFavorPointRule
                {
                    ItemId = values[index],
                    MinimumPointGain = values[index + 1],
                    MaximumPointGain = values[index + 2],
                });
            }
        }

        private static void ParseDecayRules(ScriptNode node, NpcFavorSystemFile result)
        {
            var values = PvfScriptValueReader.ReadIntegers(node, result.Content);
            for (var index = 0; index + 2 < values.Count; index += 3)
            {
                result.DecayRules.Add(new NpcFavorDecayRule
                {
                    FavorLevel = values[index],
                    InactiveDays = values[index + 1],
                    PointLoss = values[index + 2],
                });
            }
        }
    }
}
