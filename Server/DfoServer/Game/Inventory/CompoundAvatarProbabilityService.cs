using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    // 86JP装扮合成(2件套)概率判定。算法思路参考台服 WongWork::CCompoundAvatar::_ProcCompoundCore
    // 反编译结果, 数据格式按86JP实际PVF(compoundavatar_<job>.etc)简化为两段式权重池:
    // [xxx avatar] 块第1个数字=稀有池条数N, 紧跟N对(itemId,weight)是稀有时装(权重不等);
    // 剩余的(itemId,weight)是高级时装池(权重均1000, 均匀随机)。
    // 见 Docs/TASKLOG.md 第3节。
    public static class CompoundAvatarProbabilityService
    {
        private const int MaterialNormal = 21;
        private const int MaterialUpper = 899;
        private const int MaterialMaster = 10008013;

        // Etc/ServerParameter.etc 真实数值, 万分比单位。
        // [upper bind cube rate bonus]=200  -> 黄金合成器(899)加成, 台服原版字段, 反编译确认。
        // [master bind cube rate bonus]=400 -> 钻石合成器(10008013)加成, 86JP自有字段(台服没有此键,
        // 台服对应的material==3分支用的是[lower bind cube rate bonus]做概率折扣, 但那个分支在台服
        // 原始设计里对应material_lower这个不同概念, 86JP把钻石/master套用台服material==3的折扣公式
        // 会导致钻石概率反而比普通合成器低, 因此钻石改用与黄金一致的加法叠加, 数值取自86JP真实存档)。
        private const int UpperBindCubeRateBonus = 200;
        private const int MasterBindCubeRateBonus = 400;

        // 三档基础概率: 台服原版只有"两件都是普通grade"用rareRate, 否则一律用upperRareRate(混合和
        // 双高级不区分)。86JP需求要求三档严格递增, 故新增"双高级"档(rare+upper叠加), 并让"混合"档
        // 取普通档与双高级档的中点(rare + upper/2), 避免混合档低于纯普通档这种不合理结果。

        private static readonly Lazy<Dictionary<int, string>> JobIndexToFile = new Lazy<Dictionary<int, string>>(LoadJobIndex);
        private static readonly Dictionary<string, CompoundAvatarFile> FileCache = new Dictionary<string, CompoundAvatarFile>();
        private static readonly object CacheLock = new object();
        private static readonly string[] PartNames =
        {
            "hat", "hair", "face", "neck", "coat", "pants", "belt", "shoes"
        };

        public sealed class Result
        {
            public bool Success { get; set; }
            // 永远包含客户端请求的目标itemId; 命中稀有池时额外追加1个稀有装扮itemId。
            public List<int> NewItemIds { get; set; }
            public string FailReason { get; set; }
        }

        // job: characters.job 字段(0-based, 0=Swordman...12=Knight)。
        // oldItemId1/2: 被消耗的两件旧时装itemId。consumeMaterialId: 消耗品(21/899/10008013)的item_template_id。
        // requestedItemId: 客户端请求体里的目标itemId, 始终会被返回; 命中稀有池时额外多产出一件。
        public static Result Resolve(byte job, int oldItemId1, int oldItemId2, int consumeMaterialId, int requestedItemId)
        {
            var file = LoadCompoundAvatarFile(job);
            if (file == null)
                return new Result { Success = false, FailReason = $"no compoundavatar config for job={job}" };

            var part = ResolvePartName(file, requestedItemId);
            if (part == null)
                return new Result { Success = false, FailReason = $"item {requestedItemId} not found in any avatar part pool" };

            if (!file.Parts.TryGetValue(part, out var pool))
                return new Result { Success = false, FailReason = $"part '{part}' has no pool" };

            int grade1 = ItemMetadataResolver.Resolve(oldItemId1).Grade;
            int grade2 = ItemMetadataResolver.Resolve(oldItemId2).Grade;
            bool isGrade1 = grade1 == file.Grade;
            bool isGrade2 = grade2 == file.Grade;

            file.RareRate.TryGetValue(part, out var rareRate);
            file.UpperRareRate.TryGetValue(part, out var upperRareRate);

            int rate;
            if (isGrade1 && isGrade2)
                rate = rareRate;                          // 普通+普通
            else if (!isGrade1 && !isGrade2)
                rate = rareRate + upperRareRate;           // 高级+高级
            else
                rate = rareRate + upperRareRate / 2;       // 普通+高级(混合, 取中点)

            if (consumeMaterialId == MaterialUpper)
                rate += UpperBindCubeRateBonus;
            else if (consumeMaterialId == MaterialMaster)
                rate += MasterBindCubeRateBonus;

            var roll = ServerRandom.Next(10000);
            var hitRare = roll < rate && pool.RarePool.Count > 0;

            var resultItemIds = new List<int> { requestedItemId };
            if (hitRare)
                resultItemIds.Add(WeightedPick(pool.RarePool));

            FileLogger.Log($"  [CompoundAvatarProb] job={job} part={part} grade=({grade1},{grade2}) " +
                            $"rate={rate} roll={roll} hitRare={hitRare} -> items=[{string.Join(",", resultItemIds)}]");

            return new Result { Success = true, NewItemIds = resultItemIds };
        }

        private static int WeightedPick(List<(int ItemId, int Weight)> pool)
        {
            int total = 0;
            foreach (var (_, weight) in pool) total += weight;
            if (total <= 0) return pool[0].ItemId;

            var roll = ServerRandom.Next(total);
            int acc = 0;
            foreach (var (itemId, weight) in pool)
            {
                acc += weight;
                if (roll < acc) return itemId;
            }
            return pool[pool.Count - 1].ItemId;
        }

        private static string ResolvePartName(CompoundAvatarFile file, int requestedItemId)
        {
            foreach (var part in PartNames)
            {
                if (!file.Parts.TryGetValue(part, out var pool)) continue;
                foreach (var (itemId, _) in pool.RarePool)
                    if (itemId == requestedItemId) return part;
                foreach (var (itemId, _) in pool.UpperPool)
                    if (itemId == requestedItemId) return part;
            }
            return null;
        }

        private static CompoundAvatarFile LoadCompoundAvatarFile(byte job)
        {
            if (!JobIndexToFile.Value.TryGetValue(job + 1, out var fileName))
                return null;

            lock (CacheLock)
            {
                if (FileCache.TryGetValue(fileName, out var cached))
                    return cached;

                var content = PvfArchiveAccessor.ReadText("etc/" + fileName);
                var parsed = CompoundAvatarFile.Parse(content);
                FileCache[fileName] = parsed;
                return parsed;
            }
        }

        private static Dictionary<int, string> LoadJobIndex()
        {
            var map = new Dictionary<int, string>();
            string content;
            try
            {
                content = PvfArchiveAccessor.ReadText("etc/compoundavatar.etc");
            }
            catch (FileNotFoundException)
            {
                return map;
            }

            foreach (Match m in Regex.Matches(content ?? "", @"(\d+)\s+`([^`]+)`"))
            {
                if (int.TryParse(m.Groups[1].Value, out var idx))
                    map[idx] = m.Groups[2].Value;
            }
            return map;
        }
    }
}
