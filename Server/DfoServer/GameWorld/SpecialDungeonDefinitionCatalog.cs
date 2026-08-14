using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld
{
    internal enum SpecialDungeonKind
    {
        None,
        SeaChase,
        TimeCrack,
        SeizeMoney,
        SealForest,
        GentInfiltrate,
    }

    internal sealed class SpecialDungeonDefinition
    {
        internal SpecialDungeonDefinition(
            int dungeonId,
            SpecialDungeonKind kind,
            int timerSeconds,
            SeizeMoneyDefinition seizeMoney,
            SeaChaseDefinition seaChase,
            TimeCrackDefinition timeCrack,
            SealForestDefinition sealForest)
        {
            DungeonId = dungeonId;
            Kind = kind;
            TimerSeconds = timerSeconds;
            SeizeMoney = seizeMoney ?? SeizeMoneyDefinition.Default;
            SeaChase = seaChase ?? SeaChaseDefinition.Empty;
            TimeCrack = timeCrack ?? TimeCrackDefinition.Default;
            SealForest = sealForest ?? SealForestDefinition.Empty;
        }

        internal int DungeonId { get; }
        internal SpecialDungeonKind Kind { get; }
        internal int TimerSeconds { get; }
        internal SeizeMoneyDefinition SeizeMoney { get; }
        internal SeaChaseDefinition SeaChase { get; }
        internal TimeCrackDefinition TimeCrack { get; }
        internal SealForestDefinition SealForest { get; }
    }

    internal sealed class SpecialDungeonDefinitionBuilder
    {
        internal int SeizeMoneyGaugeMax { get; set; } = 1000;
        internal int SeizeMoneyGaugeSubOnDamage { get; set; } = 100;
        internal int SeizeMoneyGaugeValueToMoveHiddenMap { get; set; } = 1;
        internal string SeizeMoneyNoticeTextOnHit { get; set; } = string.Empty;
        internal int SeizeMoneyNoticeTextOnHitTermMs { get; set; } = 10000;
        internal int SeizeMoneyCreateGoldBallNumOnHitStatue { get; set; } = 3;

        internal int SeaChasePassEndPos { get; set; }
        internal List<int> SeaChaseSuccessBuffIds { get; } = new List<int>();
        internal List<int> SeaChaseFailBuffIds { get; } = new List<int>();
        internal Dictionary<int, SeaChaseBuffNotice> SeaChaseBuffNotices { get; }
            = new Dictionary<int, SeaChaseBuffNotice>();

        internal List<int> TimeCrackInvincibleMonsterCodes { get; }
            = new List<int>();
        internal List<TimeCrackBuffWeight> TimeCrackBuffWeights { get; }
            = new List<TimeCrackBuffWeight>();
        internal int TimeCrackSandGaugeMax { get; set; } = 100;
        internal int TimeCrackSandGaugeGainOnKill { get; set; } = 10;
        internal int TimeCrackSandGaugeGainOnChampion { get; set; } = 30;

        internal Dictionary<int, SealForestBuffEntry> SealForestBuffs { get; }
            = new Dictionary<int, SealForestBuffEntry>();

        internal SpecialDungeonDefinition Build(
            int dungeonId,
            SpecialDungeonKind kind,
            int timerSeconds = 0)
        {
            return new SpecialDungeonDefinition(
                dungeonId,
                kind,
                timerSeconds,
                new SeizeMoneyDefinition(
                    SeizeMoneyGaugeMax,
                    SeizeMoneyGaugeSubOnDamage,
                    SeizeMoneyGaugeValueToMoveHiddenMap,
                    SeizeMoneyNoticeTextOnHit,
                    SeizeMoneyNoticeTextOnHitTermMs,
                    SeizeMoneyCreateGoldBallNumOnHitStatue),
                new SeaChaseDefinition(
                    SeaChasePassEndPos,
                    SeaChaseSuccessBuffIds,
                    SeaChaseFailBuffIds,
                    SeaChaseBuffNotices),
                new TimeCrackDefinition(
                    TimeCrackInvincibleMonsterCodes,
                    TimeCrackBuffWeights,
                    TimeCrackSandGaugeMax,
                    TimeCrackSandGaugeGainOnKill,
                    TimeCrackSandGaugeGainOnChampion),
                new SealForestDefinition(SealForestBuffs));
        }
    }

    internal sealed class SpecialDungeonDefinitionCatalog
    {
        private static readonly Lazy<SpecialDungeonDefinitionCatalog> Cached =
            new Lazy<SpecialDungeonDefinitionCatalog>(Load);

        private readonly IReadOnlyDictionary<int, SpecialDungeonDefinition>
            _definitions;

        private SpecialDungeonDefinitionCatalog(
            IDictionary<int, SpecialDungeonDefinition> definitions)
        {
            _definitions = new ReadOnlyDictionary<int, SpecialDungeonDefinition>(
                new Dictionary<int, SpecialDungeonDefinition>(definitions));
        }

        internal static bool TryGet(
            int dungeonId,
            out SpecialDungeonDefinition definition)
            => Cached.Value._definitions.TryGetValue(dungeonId, out definition);

        internal static IReadOnlyDictionary<int, int>
            ParseGentInfiltrateTowerRequirements(string condition)
        {
            var result = new Dictionary<int, int>();
            var tokens = ScriptValueTokenizer.Tokenize(condition);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (!string.Equals(
                        tokens[index],
                        "[hunt monster]",
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
                    if (!int.TryParse(tokens[position], out var monsterCode)
                        || monsterCode <= 0)
                    {
                        continue;
                    }

                    var required = 1;
                    if (int.TryParse(
                            tokens[position + 2],
                            out var parsedRequired)
                        && parsedRequired > 0)
                    {
                        required = parsedRequired;
                    }

                    result[monsterCode] = required;
                }

                break;
            }

            return new ReadOnlyDictionary<int, int>(result);
        }

        private static SpecialDungeonDefinitionCatalog Load()
        {
            var definitions = new Dictionary<int, SpecialDungeonDefinition>();
            try
            {
                var builder = new SpecialDungeonDefinitionBuilder();
                var timerSecondsByDungeonId = new Dictionary<int, int>();
                var kindByDungeonId = new Dictionary<int, SpecialDungeonKind>();
                var text = PvfArchiveAccessor.ReadText(
                    "Etc/GameMode/SpecialDungeonModule.etc");
                var root = new ScriptParser().Parse(text);

                ParseSeaChase(root.GetChild("sea chase"), text, builder);
                ParseTimeCrack(root.GetChild("time crack"), text, builder);
                ParseSeizeMoney(root.GetChild("seize money"), text, builder);
                ParseSealForest(root.GetChild("seal forest"), text, builder);
                ParseTimerInfo(
                    root.GetChild("etc"),
                    text,
                    timerSecondsByDungeonId);
                ParseTimerInfo(root, text, timerSecondsByDungeonId);
                ParseTimerInfoFromText(text, timerSecondsByDungeonId);
                ParseDungeonKinds(kindByDungeonId);

                foreach (var pair in kindByDungeonId)
                {
                    timerSecondsByDungeonId.TryGetValue(
                        pair.Key,
                        out var timerSeconds);
                    definitions[pair.Key] = builder.Build(
                        pair.Key,
                        pair.Value,
                        timerSeconds);
                }

                FileLogger.Log(
                    $"[SpecialDungeonDefinitions] loaded: " +
                    $"seaChaseSuccessBuffs={builder.SeaChaseSuccessBuffIds.Count} " +
                    $"seaChaseFailBuffs={builder.SeaChaseFailBuffIds.Count} " +
                    $"timeCrackBuffs={builder.TimeCrackBuffWeights.Count} " +
                    $"timeCrackSandGauge={builder.TimeCrackSandGaugeMax}/" +
                    $"{builder.TimeCrackSandGaugeGainOnKill}/" +
                    $"{builder.TimeCrackSandGaugeGainOnChampion} " +
                    $"seizeGauge={builder.SeizeMoneyGaugeMax} " +
                    $"sealBuffs={builder.SealForestBuffs.Count} " +
                    $"specialDungeons={definitions.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonDefinitions] load failed: {ex.Message}");
            }

            return new SpecialDungeonDefinitionCatalog(definitions);
        }

        private static void ParseSeaChase(
            ScriptNode node,
            string text,
            SpecialDungeonDefinitionBuilder builder)
        {
            if (node == null)
                return;

            builder.SeaChasePassEndPos = ReadInt(
                node,
                text,
                "pass end pos",
                0);
            ReadIntList(
                node,
                text,
                "success buff",
                builder.SeaChaseSuccessBuffIds);
            ReadIntList(
                node,
                text,
                "fail buff",
                builder.SeaChaseFailBuffIds);

            var notice = node.GetChild("buff notice string");
            var tokens = ScriptValueTokenizer.Tokenize(
                notice?.GetFirstDataContent(text));
            for (var index = 0; index + 3 < tokens.Count; index += 4)
            {
                if (!int.TryParse(tokens[index], out var buffId))
                    continue;

                builder.SeaChaseBuffNotices[buffId] =
                    new SeaChaseBuffNotice(
                        buffId,
                        tokens[index + 1],
                        tokens[index + 2],
                        tokens[index + 3]);
            }
        }

        private static void ParseTimeCrack(
            ScriptNode node,
            string text,
            SpecialDungeonDefinitionBuilder builder)
        {
            if (node == null)
                return;

            ReadIntList(
                node,
                text,
                "InvincibleMonster",
                builder.TimeCrackInvincibleMonsterCodes);
            builder.TimeCrackBuffWeights.Clear();
            var tokens = ScriptValueTokenizer.Tokenize(
                node.GetChild("buff weight")?.GetFirstDataContent(text));
            for (var index = 0; index + 1 < tokens.Count; index += 2)
            {
                if (int.TryParse(tokens[index], out var buffId)
                    && int.TryParse(tokens[index + 1], out var weight)
                    && buffId > 0
                    && weight > 0)
                {
                    builder.TimeCrackBuffWeights.Add(
                        new TimeCrackBuffWeight(buffId, weight));
                }
            }

            var gaugeTokens = ScriptValueTokenizer.Tokenize(
                node.GetChild("sand gauge")?.GetFirstDataContent(text));
            if (gaugeTokens.Count > 0
                && int.TryParse(gaugeTokens[0], out var max)
                && max > 0)
            {
                builder.TimeCrackSandGaugeMax = max;
            }
            if (gaugeTokens.Count > 1
                && int.TryParse(gaugeTokens[1], out var gain)
                && gain > 0)
            {
                builder.TimeCrackSandGaugeGainOnKill = gain;
            }
            if (gaugeTokens.Count > 2
                && int.TryParse(gaugeTokens[2], out var championGain)
                && championGain > 0)
            {
                builder.TimeCrackSandGaugeGainOnChampion = championGain;
            }
        }

        private static void ParseSeizeMoney(
            ScriptNode node,
            string text,
            SpecialDungeonDefinitionBuilder builder)
        {
            if (node == null)
                return;

            builder.SeizeMoneyGaugeMax = ReadInt(
                node,
                text,
                "gauge max",
                1000);
            builder.SeizeMoneyGaugeSubOnDamage = ReadInt(
                node,
                text,
                "gauge sub on damage",
                100);
            builder.SeizeMoneyGaugeValueToMoveHiddenMap = ReadInt(
                node,
                text,
                "gauge value to move hidden map",
                1);
            builder.SeizeMoneyNoticeTextOnHit = ReadBacktickText(
                node,
                text,
                "notice text on hit");
            builder.SeizeMoneyNoticeTextOnHitTermMs = ReadInt(
                node,
                text,
                "notice text on hit time term",
                10000);
            builder.SeizeMoneyCreateGoldBallNumOnHitStatue = ReadInt(
                node,
                text,
                "create gold ball num on hit statue",
                3);
        }

        private static void ParseSealForest(
            ScriptNode node,
            string text,
            SpecialDungeonDefinitionBuilder builder)
        {
            var addBuff = node?.GetChild("add buff");
            if (addBuff == null)
                return;

            var tokens = ScriptValueTokenizer.Tokenize(
                addBuff.GetFirstDataContent(text));
            for (var index = 0; index + 4 < tokens.Count;)
            {
                if (!int.TryParse(tokens[index], out var monsterCode)
                    || !int.TryParse(tokens[index + 1], out var buffId))
                {
                    index++;
                    continue;
                }

                builder.SealForestBuffs[monsterCode] =
                    new SealForestBuffEntry(
                        monsterCode,
                        buffId,
                        tokens[index + 2],
                        tokens[index + 3],
                        tokens[index + 4]);
                index += 5;
            }
        }

        private static void ParseTimerInfo(
            ScriptNode node,
            string text,
            IDictionary<int, int> timers)
        {
            var timerInfo = node?.GetChild("timer info");
            if (timerInfo == null)
                return;

            ParseTimerTokens(
                ScriptValueTokenizer.Tokenize(
                    timerInfo.GetFirstDataContent(text)),
                timers);
        }

        private static void ParseTimerInfoFromText(
            string text,
            IDictionary<int, int> timers)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var match = Regex.Match(
                text,
                @"(?im)^\s*\[timer info\]\s*(?<inline>[^\r\n]*)$");
            if (!match.Success)
                return;

            var lines = new List<string>();
            var inline = match.Groups["inline"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inline))
                lines.Add(inline);

            var lineStart = text.IndexOf('\n', match.Index + match.Length);
            if (lineStart >= 0)
            {
                var position = lineStart + 1;
                while (position < text.Length)
                {
                    var next = text.IndexOf('\n', position);
                    if (next < 0)
                        next = text.Length;

                    var line = text.Substring(position, next - position).Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                        break;
                    if (line.Length > 0
                        && !line.StartsWith("#", StringComparison.Ordinal))
                    {
                        lines.Add(line);
                    }

                    position = next + 1;
                }
            }

            ParseTimerTokens(
                ScriptValueTokenizer.Tokenize(string.Join(" ", lines)),
                timers);
        }

        private static void ParseTimerTokens(
            IReadOnlyList<string> tokens,
            IDictionary<int, int> timers)
        {
            for (var index = 0; index + 1 < tokens.Count; index += 2)
            {
                if (int.TryParse(tokens[index], out var dungeonId)
                    && int.TryParse(tokens[index + 1], out var seconds))
                {
                    timers[dungeonId] = seconds;
                }
            }
        }

        private static void ParseDungeonKinds(
            IDictionary<int, SpecialDungeonKind> kinds)
        {
            var list = LstFile.Parse(
                PvfArchiveAccessor.ReadText("dungeon/dungeon.lst"));
            foreach (var entry in list.Entries)
            {
                var path = (entry.FilePath ?? string.Empty).Replace('\\', '/');
                if (!path.StartsWith(
                        "Special/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var kind = ResolveKindFromPath(path);
                if (kind != SpecialDungeonKind.None)
                    kinds[entry.Id] = kind;
            }
        }

        private static SpecialDungeonKind ResolveKindFromPath(string path)
        {
            if (path.IndexOf("SeaChase", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.SeaChase;
            if (path.IndexOf("TimeBreak", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.TimeCrack;
            if (path.IndexOf("Seizemoney", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.SeizeMoney;
            if (path.IndexOf("SealForest", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.SealForest;
            if (path.IndexOf("GentInfiltrate", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.GentInfiltrate;
            return SpecialDungeonKind.None;
        }

        private static int ReadInt(
            ScriptNode parent,
            string text,
            string tag,
            int fallback)
        {
            var node = parent?.GetChild(tag);
            return node != null
                && int.TryParse(node.GetFirstDataContent(text), out var value)
                    ? value
                    : fallback;
        }

        private static void ReadIntList(
            ScriptNode parent,
            string text,
            string tag,
            ICollection<int> values)
        {
            values.Clear();
            var node = parent?.GetChild(tag);
            if (node == null)
                return;

            foreach (var token in ScriptValueTokenizer.Tokenize(
                node.GetFirstDataContent(text)))
            {
                if (int.TryParse(token, out var value))
                    values.Add(value);
            }
        }

        private static string ReadBacktickText(
            ScriptNode parent,
            string text,
            string tag)
        {
            var node = parent?.GetChild(tag);
            var tokens = ScriptValueTokenizer.Tokenize(
                node?.GetFirstDataContent(text));
            return tokens.Count > 0 ? tokens[0] : string.Empty;
        }
    }

    internal sealed class SeizeMoneyDefinition
    {
        internal static readonly SeizeMoneyDefinition Default =
            new SeizeMoneyDefinition(1000, 100, 1, string.Empty, 10000, 3);

        internal SeizeMoneyDefinition(
            int gaugeMax,
            int gaugeSubOnDamage,
            int gaugeValueToMoveHiddenMap,
            string noticeTextOnHit,
            int noticeTextOnHitTermMs,
            int createGoldBallNumOnHitStatue)
        {
            GaugeMax = gaugeMax;
            GaugeSubOnDamage = gaugeSubOnDamage;
            GaugeValueToMoveHiddenMap = gaugeValueToMoveHiddenMap;
            NoticeTextOnHit = noticeTextOnHit ?? string.Empty;
            NoticeTextOnHitTermMs = noticeTextOnHitTermMs;
            CreateGoldBallNumOnHitStatue = createGoldBallNumOnHitStatue;
        }

        internal int GaugeMax { get; }
        internal int GaugeSubOnDamage { get; }
        internal int GaugeValueToMoveHiddenMap { get; }
        internal string NoticeTextOnHit { get; }
        internal int NoticeTextOnHitTermMs { get; }
        internal int CreateGoldBallNumOnHitStatue { get; }
    }

    internal sealed class SeaChaseDefinition
    {
        internal static readonly SeaChaseDefinition Empty =
            new SeaChaseDefinition(
                0,
                Array.Empty<int>(),
                Array.Empty<int>(),
                new Dictionary<int, SeaChaseBuffNotice>());

        internal SeaChaseDefinition(
            int passEndPos,
            IEnumerable<int> successBuffIds,
            IEnumerable<int> failBuffIds,
            IDictionary<int, SeaChaseBuffNotice> buffNotices)
        {
            PassEndPos = passEndPos;
            SuccessBuffIds = Array.AsReadOnly(
                new List<int>(successBuffIds ?? Array.Empty<int>()).ToArray());
            FailBuffIds = Array.AsReadOnly(
                new List<int>(failBuffIds ?? Array.Empty<int>()).ToArray());
            BuffNotices = new ReadOnlyDictionary<int, SeaChaseBuffNotice>(
                new Dictionary<int, SeaChaseBuffNotice>(
                    buffNotices
                    ?? new Dictionary<int, SeaChaseBuffNotice>()));
        }

        internal int PassEndPos { get; }
        internal IReadOnlyList<int> SuccessBuffIds { get; }
        internal IReadOnlyList<int> FailBuffIds { get; }
        internal IReadOnlyDictionary<int, SeaChaseBuffNotice> BuffNotices { get; }
    }

    internal sealed class SeaChaseBuffNotice
    {
        internal SeaChaseBuffNotice(
            int buffId,
            string messageA,
            string messageB,
            string color)
        {
            BuffId = buffId;
            MessageA = messageA ?? string.Empty;
            MessageB = messageB ?? string.Empty;
            Color = color ?? string.Empty;
        }

        internal int BuffId { get; }
        internal string MessageA { get; }
        internal string MessageB { get; }
        internal string Color { get; }
    }

    internal sealed class TimeCrackDefinition
    {
        internal static readonly TimeCrackDefinition Default =
            new TimeCrackDefinition(
                Array.Empty<int>(),
                Array.Empty<TimeCrackBuffWeight>(),
                100,
                10,
                30);

        internal TimeCrackDefinition(
            IEnumerable<int> invincibleMonsterCodes,
            IEnumerable<TimeCrackBuffWeight> buffWeights,
            int sandGaugeMax,
            int sandGaugeGainOnKill,
            int sandGaugeGainOnChampion)
        {
            InvincibleMonsterCodes = Array.AsReadOnly(
                new List<int>(
                    invincibleMonsterCodes ?? Array.Empty<int>()).ToArray());
            BuffWeights = Array.AsReadOnly(
                new List<TimeCrackBuffWeight>(
                    buffWeights
                    ?? Array.Empty<TimeCrackBuffWeight>()).ToArray());
            SandGaugeMax = sandGaugeMax;
            SandGaugeGainOnKill = sandGaugeGainOnKill;
            SandGaugeGainOnChampion = sandGaugeGainOnChampion;
        }

        internal IReadOnlyList<int> InvincibleMonsterCodes { get; }
        internal IReadOnlyList<TimeCrackBuffWeight> BuffWeights { get; }
        internal int SandGaugeMax { get; }
        internal int SandGaugeGainOnKill { get; }
        internal int SandGaugeGainOnChampion { get; }
    }

    internal sealed class TimeCrackBuffWeight
    {
        internal TimeCrackBuffWeight(int buffId, int weight)
        {
            BuffId = buffId;
            Weight = weight;
        }

        internal int BuffId { get; }
        internal int Weight { get; }
    }

    internal sealed class SealForestDefinition
    {
        internal static readonly SealForestDefinition Empty =
            new SealForestDefinition(
                new Dictionary<int, SealForestBuffEntry>());

        internal SealForestDefinition(
            IDictionary<int, SealForestBuffEntry> buffsByMonsterCode)
        {
            BuffsByMonsterCode =
                new ReadOnlyDictionary<int, SealForestBuffEntry>(
                    new Dictionary<int, SealForestBuffEntry>(
                        buffsByMonsterCode
                        ?? new Dictionary<int, SealForestBuffEntry>()));
        }

        internal IReadOnlyDictionary<int, SealForestBuffEntry>
            BuffsByMonsterCode { get; }
    }

    internal sealed class SealForestBuffEntry
    {
        internal SealForestBuffEntry(
            int monsterCode,
            int buffId,
            string messageA,
            string messageB,
            string color)
        {
            MonsterCode = monsterCode;
            BuffId = buffId;
            MessageA = messageA ?? string.Empty;
            MessageB = messageB ?? string.Empty;
            Color = color ?? string.Empty;
        }

        internal int MonsterCode { get; }
        internal int BuffId { get; }
        internal string MessageA { get; }
        internal string MessageB { get; }
        internal string Color { get; }
    }
}
