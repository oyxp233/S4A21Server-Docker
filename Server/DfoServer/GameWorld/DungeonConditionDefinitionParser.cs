using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.GameWorld
{
    internal static class DungeonConditionDefinitionParser
    {
        internal static IReadOnlyList<int> ParseMonsterCodes(
            string condition,
            string sectionTag)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(sectionTag))
                return result.AsReadOnly();

            var tokens = ScriptValueTokenizer.Tokenize(condition);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (!string.Equals(
                        tokens[index],
                        sectionTag,
                        StringComparison.OrdinalIgnoreCase)
                    || index + 1 >= tokens.Count
                    || !int.TryParse(tokens[index + 1], out var count)
                    || count <= 0)
                {
                    continue;
                }

                var position = index + 2;
                for (var item = 0;
                    item < count && position + 2 < tokens.Count;
                    item++, position += 3)
                {
                    if (int.TryParse(tokens[position], out var monsterCode)
                        && monsterCode > 0)
                    {
                        result.Add(monsterCode);
                    }
                }

                break;
            }

            return new ReadOnlyCollection<int>(result);
        }
    }
}
