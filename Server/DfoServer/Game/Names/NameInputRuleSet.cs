using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Names
{
    internal sealed class NameInputRuleSet
    {
        private static readonly Lazy<NameInputRuleSet> CurrentRules =
            new Lazy<NameInputRuleSet>(LoadFromPvf);

        private readonly List<Range> _allowedUnicodeRanges = new List<Range>();
        private readonly List<ByteRange> _oneByteRanges = new List<ByteRange>();
        private readonly HashSet<byte> _oneByteValues = new HashSet<byte>();
        private readonly List<Range> _twoByteRanges = new List<Range>();
        private readonly HashSet<int> _twoByteValues = new HashSet<int>();
        private readonly List<string> _slangNames = new List<string>();
        private readonly HashSet<byte> _oneByteSkipChars = new HashSet<byte>();
        private readonly HashSet<int> _twoByteSkipChars = new HashSet<int>();

        public static NameInputRuleSet Current => CurrentRules.Value;

        public bool IsAllowedByUnicodeRange(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                int codePoint;
                var ch = text[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                        return false;
                    codePoint = char.ConvertToUtf32(ch, text[++i]);
                }
                else if (char.IsLowSurrogate(ch))
                {
                    return false;
                }
                else
                {
                    codePoint = ch;
                }

                if (!IsInRange(_allowedUnicodeRanges, codePoint))
                    return false;
            }

            return true;
        }

        public bool HasSpecialCharacter(byte[] rawBytes, string text)
        {
            if (rawBytes != null)
            {
                for (var i = 0; i < rawBytes.Length; i++)
                {
                    var value = rawBytes[i];
                    if (_oneByteValues.Contains(value) || IsInByteRange(_oneByteRanges, value))
                        return true;
                }
            }

            for (var i = 0; i < text.Length; i++)
            {
                int codePoint;
                var ch = text[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                        return true;
                    codePoint = char.ConvertToUtf32(ch, text[++i]);
                }
                else if (char.IsLowSurrogate(ch))
                {
                    return true;
                }
                else
                {
                    codePoint = ch;
                }

                if (codePoint <= 0xFFFF &&
                    (_twoByteValues.Contains(codePoint) || IsInRange(_twoByteRanges, codePoint)))
                    return true;
            }

            return false;
        }

        public bool HasSlang(string text)
        {
            if (string.IsNullOrEmpty(text) || _slangNames.Count == 0)
                return false;

            text = NormalizeForSlang(text);
            for (var i = 0; i < _slangNames.Count; i++)
            {
                if (text.IndexOf(_slangNames[i], StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        private static NameInputRuleSet LoadFromPvf()
        {
            var rules = new NameInputRuleSet();
            AddDefaultUnicodeRanges(rules);

            TryLoad("invalid name", () => ParseInvalidName(ReadFirstExistingText(
                "etc/invalidname.etc",
                "etc/invalidName.etc",
                "Etc/invalidname.etc",
                "Etc/invalidName.etc"), rules));
            TryLoad("slang name", () => ParseSlangNames(ReadFirstExistingText(
                "etc/slangname.etc",
                "Etc/slangname.etc"), rules));
            TryLoad("unicode range", () =>
            {
                var pvfRanges = new List<Range>();
                ParseUnicodeRanges(ReadFirstExistingText(
                    "etc/RestrictNameUnicodeRangeTable.etc",
                    "etc/restrictnameunicoderangetable.etc",
                    "Etc/RestrictNameUnicodeRangeTable.etc"), pvfRanges);
                if (pvfRanges.Count > 0)
                {
                    rules._allowedUnicodeRanges.Clear();
                    rules._allowedUnicodeRanges.AddRange(pvfRanges);
                }
            });

            FileLogger.Log(
                "[NameInputRuleSet] loaded unicodeRanges={0}, oneByteRanges={1}, oneByteValues={2}, twoByteRanges={3}, twoByteValues={4}, slang={5}",
                rules._allowedUnicodeRanges.Count,
                rules._oneByteRanges.Count,
                rules._oneByteValues.Count,
                rules._twoByteRanges.Count,
                rules._twoByteValues.Count,
                rules._slangNames.Count);
            return rules;
        }

        private static void AddDefaultUnicodeRanges(NameInputRuleSet rules)
        {
            rules._allowedUnicodeRanges.Add(new Range(48, 57));
            rules._allowedUnicodeRanges.Add(new Range(19968, 40908));
            rules._allowedUnicodeRanges.Add(new Range(65, 90));
            rules._allowedUnicodeRanges.Add(new Range(97, 122));
        }

        private static void TryLoad(string name, Action load)
        {
            try
            {
                load();
            }
            catch (Exception ex)
            {
                FileLogger.Log("[NameInputRuleSet] {0} rules unavailable: {1}", name, ex.Message);
            }
        }

        private static string ReadFirstExistingText(params string[] paths)
        {
            for (var i = 0; i < paths.Length; i++)
            {
                try
                {
                    return PvfArchiveAccessor.ReadText(paths[i]);
                }
                catch
                {
                }
            }

            throw new InvalidOperationException("PVF rule file not found.");
        }

        private static void ParseInvalidName(string content, NameInputRuleSet rules)
        {
            string section = null;
            foreach (var line in EnumerateRuleLines(content))
            {
                var tag = TryGetTag(line);
                if (tag != null)
                {
                    section = tag;
                    continue;
                }

                var values = ParseInts(line);
                if (values.Count == 0 || section == null)
                    continue;

                switch (section)
                {
                    case "one byte range":
                        AddByteRanges(values, rules._oneByteRanges);
                        break;
                    case "one byte":
                        for (var i = 0; i < values.Count; i++)
                            rules._oneByteValues.Add((byte)Clamp(values[i], 0, 255));
                        break;
                    case "two byte range":
                        AddRanges(values, rules._twoByteRanges);
                        break;
                    case "two byte":
                        for (var i = 0; i < values.Count; i++)
                            rules._twoByteValues.Add(Clamp(values[i], 0, 0xFFFF));
                        break;
                }
            }
        }

        private static void ParseUnicodeRanges(string content, List<Range> target)
        {
            string section = null;
            foreach (var line in EnumerateRuleLines(content))
            {
                var tag = TryGetTag(line);
                if (tag != null)
                {
                    section = tag == "allow unicode range" || tag == "/allow unicode range"
                        ? null
                        : tag;
                    continue;
                }

                if (section != "range")
                    continue;

                var values = ParseInts(line);
                if (values.Count >= 2)
                    target.Add(new Range(values[0], values[1]));
            }
        }

        private static void ParseSlangNames(string content, NameInputRuleSet rules)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string section = null;
            foreach (var line in EnumerateRuleLines(content))
            {
                var tag = TryGetTag(line);
                if (tag != null)
                {
                    section = tag;
                    continue;
                }

                if (section == "one byte skip char")
                {
                    var values = ParseInts(line);
                    for (var i = 0; i < values.Count; i++)
                        rules._oneByteSkipChars.Add((byte)Clamp(values[i], 0, 255));
                    continue;
                }

                if (section == "two byte skip char")
                {
                    var values = ParseInts(line);
                    for (var i = 0; i < values.Count; i++)
                        rules._twoByteSkipChars.Add(Clamp(values[i], 0, 0xFFFF));
                    continue;
                }

                if (section != "SLANG")
                    continue;

                var matches = Regex.Matches(line, "`([^`]*)`");
                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                        AddSlang(match.Groups[1].Value, rules, seen);
                    continue;
                }

                var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < tokens.Length; i++)
                {
                    var token = StripBackticks(tokens[i].Trim());
                    if (token.Length > 0 && !int.TryParse(token, out _))
                        AddSlang(token, rules, seen);
                }
            }
        }

        private static void AddSlang(string value, NameInputRuleSet rules, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            value = value.Trim();
            if (seen.Add(value))
                rules._slangNames.Add(value);
        }

        private static IEnumerable<string> EnumerateRuleLines(string content)
        {
            if (string.IsNullOrEmpty(content))
                yield break;

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StripComment(lines[i]).Trim();
                if (line.Length > 0)
                    yield return line;
            }
        }

        private static string StripComment(string line)
        {
            if (line == null)
                return string.Empty;

            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private static string TryGetTag(string line)
        {
            if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']')
            {
                var tag = line.Substring(1, line.Length - 2).Trim();
                if (tag.Length > 0)
                    return tag;
            }

            return null;
        }

        private static List<int> ParseInts(string line)
        {
            var values = new List<int>();
            var matches = Regex.Matches(line, @"[-+]?\d+");
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Value, out var value))
                    values.Add(value);
            }

            return values;
        }

        private static void AddByteRanges(List<int> values, List<ByteRange> target)
        {
            for (var i = 0; i + 1 < values.Count; i += 2)
                target.Add(new ByteRange((byte)Clamp(values[i], 0, 255), (byte)Clamp(values[i + 1], 0, 255)));
        }

        private static void AddRanges(List<int> values, List<Range> target)
        {
            for (var i = 0; i + 1 < values.Count; i += 2)
                target.Add(new Range(values[i], values[i + 1]));
        }

        private static bool IsInRange(List<Range> ranges, int value)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                if (ranges[i].Contains(value))
                    return true;
            }

            return false;
        }

        private static bool IsInByteRange(List<ByteRange> ranges, byte value)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                if (ranges[i].Contains(value))
                    return true;
            }

            return false;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static string StripBackticks(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                return value.Substring(1, value.Length - 2);
            return value ?? string.Empty;
        }

        private string NormalizeForSlang(string text)
        {
            if ((_oneByteSkipChars.Count == 0 && _twoByteSkipChars.Count == 0) || string.IsNullOrEmpty(text))
                return text;

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                var code = (int)ch;
                if (code <= 0xFF && _oneByteSkipChars.Contains((byte)code))
                    continue;
                if (code <= 0xFFFF && _twoByteSkipChars.Contains(code))
                    continue;
                builder.Append(ch);
            }

            return builder.ToString();
        }

        private readonly struct Range
        {
            private readonly int _left;
            private readonly int _right;

            public Range(int left, int right)
            {
                _left = Math.Min(left, right);
                _right = Math.Max(left, right);
            }

            public bool Contains(int value) => value >= _left && value <= _right;
        }

        private readonly struct ByteRange
        {
            private readonly byte _left;
            private readonly byte _right;

            public ByteRange(byte left, byte right)
            {
                _left = left <= right ? left : right;
                _right = left <= right ? right : left;
            }

            public bool Contains(byte value) => value >= _left && value <= _right;
        }
    }
}
