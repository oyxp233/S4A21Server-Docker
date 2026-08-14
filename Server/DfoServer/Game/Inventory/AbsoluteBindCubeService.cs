using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Inventory
{
    // 绝对合成器(8件高级装扮100%合成指定天空套部位, 如"旷古天娇"/"九天霜华"系列)的服务端校验。
    // 消耗品PVF [action type] `[absolute bind cube]` key 8 2 0 0 的key, 对应
    // etc/chn_absolute_bind_cube.etc 里 [absolute bind cube] key ... [/absolute bind cube] 区块,
    // 该区块按职业(PVF英文标签)列出8个部位各自的目标itemId(100%确定, 非随机)。
    // 客户端弹窗选择部位后, 直接把对应itemId放进0x03EA请求体, 服务端在这里反查同一份数据校验
    // 该itemId确实属于"consumeMaterialId这个合成器+这个职业"的合法产出集合, 避免信任客户端任意值。
    // 见 Docs/TASKLOG.md。
    public static class AbsoluteBindCubeService
    {
        private static readonly string[] JobLabels =
        {
            "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]",
            "[at gunner]", "[thief]", "[at fighter]", "[at mage]",
            "[demonic swordman]", "[creator mage]", "[at swordman]", "[knight]"
        };

        private static readonly Lazy<LstFile> StackableList = new Lazy<LstFile>(
            () => LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst")));

        private static readonly Lazy<AbsoluteBindCubeFile> CubeFile = new Lazy<AbsoluteBindCubeFile>(
            () => AbsoluteBindCubeFile.Parse(PvfArchiveAccessor.ReadText("etc/chn_absolute_bind_cube.etc")));

        public sealed class Result
        {
            public bool Success { get; set; }
            // job对应职业在该key下的全部合法(part, itemId), 通常8个部位。
            public Dictionary<string, int> PartToItemId { get; set; }
            public string FailReason { get; set; }
        }

        // consumeMaterialId: 消耗品(合成器)的item_template_id。job: characters.job(0-12)。
        public static Result Resolve(int consumeMaterialId, byte job)
        {
            var stackableEntry = StackableList.Value.GetById(consumeMaterialId);
            if (stackableEntry == null)
                return new Result { Success = false, FailReason = $"consumeMaterialId {consumeMaterialId} not found in stackable.lst" };

            StackableItemFile stk;
            try
            {
                stk = StackableItemFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("stackable", stackableEntry.FilePath)));
            }
            catch (FileNotFoundException)
            {
                return new Result { Success = false, FailReason = $"stackable file not found for {consumeMaterialId}" };
            }

            if (!string.Equals(stk.ActionTypeName, "[absolute bind cube]", StringComparison.OrdinalIgnoreCase) ||
                stk.ActionTypeParams.Count == 0)
                return new Result { Success = false, FailReason = $"item {consumeMaterialId} has no [absolute bind cube] action type" };

            var key = stk.ActionTypeParams[0];
            if (!CubeFile.Value.Cubes.TryGetValue(key, out var jobMap))
                return new Result { Success = false, FailReason = $"no absolute bind cube data for key={key}" };

            if (job >= JobLabels.Length)
                return new Result { Success = false, FailReason = $"job {job} out of range" };

            if (!jobMap.TryGetValue(JobLabels[job], out var partToItemId))
                return new Result { Success = false, FailReason = $"job {JobLabels[job]} has no avatar set for key={key} (该职业没有此天空套)" };

            return new Result { Success = true, PartToItemId = partToItemId };
        }
    }
}
