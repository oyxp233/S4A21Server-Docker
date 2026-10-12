using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

namespace DfoServer.GameWorld
{
    internal sealed record CharacterLevelUpReward(byte Level, int ItemId, int ItemCount);

    internal static class CharacterLevelUpRewardCatalog
    {
        internal const string PvfPath = "etc/serverparameter_chn.etc";
        private static readonly Lazy<IReadOnlyDictionary<byte, CharacterLevelUpReward>> Rewards =
            new(() => Parse(PvfArchiveAccessor.ReadText(PvfPath)));

        internal static IReadOnlyDictionary<byte, CharacterLevelUpReward> Get() => Rewards.Value;

        internal static IReadOnlyDictionary<byte, CharacterLevelUpReward> Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var result = new Dictionary<byte, CharacterLevelUpReward>();
            foreach (var node in new ScriptParser().Parse(text).GetChildren("level up reward"))
            {
                var tokens = new List<string>();
                foreach (var data in node.DataItems)
                    tokens.AddRange(ScriptValueTokenizer.Tokenize(data.GetContent(text)));

                // 当前 PVF 每段为 level、两个客户端文本参数、itemId、count。
                // 未闭环的文本参数不解释成职业/活动条件。
                var values = new int[5];
                if (!node.HasEndTag || node.Children.Count != 0 || tokens.Count != values.Length)
                    throw new InvalidDataException($"{PvfPath}: malformed [level up reward]");
                for (int i = 0; i < values.Length; i++)
                    if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                        throw new InvalidDataException($"{PvfPath}: invalid reward token {tokens[i]}");
                if (values[0] < 2 || values[0] > byte.MaxValue || values[3] <= 0 || values[4] <= 0)
                    throw new InvalidDataException($"{PvfPath}: invalid level/item/count");
                var level = (byte)values[0];
                if (!result.TryAdd(level, new CharacterLevelUpReward(level, values[3], values[4])))
                    throw new InvalidDataException($"{PvfPath}: duplicate reward level {level}");
            }
            return new ReadOnlyDictionary<byte, CharacterLevelUpReward>(result);
        }
    }
}
