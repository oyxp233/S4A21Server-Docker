using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DfoServer.GameWorld
{
    // 龙跃武器成长定义，唯一真源是 PVF 的 etc/equipment_grow.etc。
    //
    // 文件结构（当前 Script.pvf）：
    //   [group]
    //   1                          <- 成长组 ID
    //   [max level]
    //   7                          <- 最高星级
    //   [exp acquire]
    //   `death`                    <- 成长值来源：击杀
    //   [point] 5                  <- 每次击杀获得的基础点数
    //   [kind] 4 1 100 2 100 3 300 4 140
    //                              <- 怪物种类权重：普通/精英/超精英/领主
    //   [level correction] 3 3 10 5 90 10
    //                              <- 等级修正分段
    //   [difficulty correction] 100 100 100 100 100
    //   [exp base]
    //   1 1794 1                   <- 星级 需求经验 是否需要材料(0=要材料)
    //   [penalty] `fatigue` 30 20  <- 未消耗疲劳值的衰减规则
    //   ...
    //   7 0 0                      <- 7 星为满级，无需求
    //
    // 经验需求按星级递增（1794/3174/4830/...），客户端据此把
    // ItemCore +74 的当前值换算成进度百分比。
    internal sealed class EquipmentGrowDefinition
    {
        private readonly Dictionary<int, int> _expByStage;
        private readonly Dictionary<int, bool> _materialRequiredByStage;

        private EquipmentGrowDefinition(
            int groupId,
            int maxLevel,
            int basePoint,
            Dictionary<int, int> expByStage,
            Dictionary<int, bool> materialRequiredByStage,
            int penaltyFatigueThreshold,
            int penaltyPercent)
        {
            GroupId = groupId;
            MaxLevel = maxLevel;
            BasePoint = basePoint;
            _expByStage = expByStage;
            _materialRequiredByStage = materialRequiredByStage;
            PenaltyFatigueThreshold = penaltyFatigueThreshold;
            PenaltyPercent = penaltyPercent;
        }

        internal int GroupId { get; }

        internal int MaxLevel { get; }

        internal int BasePoint { get; }

        // 未消耗疲劳值时的阈值与衰减百分比（PVF: `fatigue` 30 20）。
        internal int PenaltyFatigueThreshold { get; }

        internal int PenaltyPercent { get; }

        // 指定星级升下一星所需的成长值。0 表示该星级已满或未定义。
        internal int GetRequiredExp(int stage)
            => _expByStage.TryGetValue(stage, out var value) ? value : 0;

        // PVF 的第三列：0 表示该星级升级需要材料（龙跃之息卷轴），1 表示免费自动升。
        // 目前 1/2 星为 1，3 星起为 0，和官方资料一致。
        internal bool RequiresMaterial(int stage)
            => !_materialRequiredByStage.TryGetValue(stage, out var free) || !free;

        private static EquipmentGrowDefinition _cached;

        internal static EquipmentGrowDefinition Default
            => _cached ?? (_cached = Load());

        private static EquipmentGrowDefinition Load()
        {
            var text = PvfArchiveAccessor.ReadText("etc/equipment_grow.etc");
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("etc/equipment_grow.etc 为空，无法确定龙跃武器成长规则。");

            var groupId = 0;
            var maxLevel = 0;
            var basePoint = 0;
            var expByStage = new Dictionary<int, int>();
            var materialByStage = new Dictionary<int, bool>();
            var penaltyThreshold = 0;
            var penaltyPercent = 0;

            string section = null;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    if (section == "exp base")
                        ReadExpBase(lines, ref i, expByStage, materialByStage,
                            ref penaltyThreshold, ref penaltyPercent);
                    else if (section == "group")
                        groupId = ReadFirstInt(lines, i);
                    else if (section == "max level")
                        maxLevel = ReadFirstInt(lines, i);
                    else if (section == "point")
                        basePoint = ReadFirstInt(lines, i);
                    continue;
                }
            }

            if (maxLevel <= 0)
                maxLevel = 7;
            if (basePoint <= 0)
                basePoint = 5;

            return new EquipmentGrowDefinition(
                groupId,
                maxLevel,
                basePoint,
                expByStage,
                materialByStage,
                penaltyThreshold,
                penaltyPercent);
        }

        // [exp base] 段里每个星级一块：star requiredExp freeAuto
        private static void ReadExpBase(
            string[] lines,
            ref int index,
            Dictionary<int, int> expByStage,
            Dictionary<int, bool> materialByStage,
            ref int penaltyThreshold,
            ref int penaltyPercent)
        {
            for (var i = index + 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("[/"))
                {
                    index = i;
                    return;
                }

                if (line.Length == 0)
                    continue;

                if (line.StartsWith("[penalty]"))
                {
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // 形如: [penalty] `fatigue` 30 20
                    if (tokens.Length >= 4
                        && int.TryParse(tokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
                        && int.TryParse(tokens[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                    {
                        penaltyThreshold = threshold;
                        penaltyPercent = percent;
                    }
                    continue;
                }

                if (line.StartsWith("["))
                    continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var star))
                    continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp))
                    continue;

                expByStage[star] = exp;
                // 第三列 1=免费自动升，0=需要材料
                materialByStage[star] = parts.Length >= 3 && parts[2] == "1";
            }
        }

        private static int ReadFirstInt(string[] lines, int headerIndex)
        {
            for (var i = headerIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                    continue;
                var token = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : 0;
            }
            return 0;
        }
    }
}
