using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class DisjointMachineResultCalculator
    {
        internal static DisjointMachineResultRule ResolveRule(
            ItemCore source,
            ItemMetadata metadata,
            byte machineGrade)
        {
            if (source == null || metadata == null || machineGrade == 0)
                return null;

            return DisjointMachineConfigProvider.Config.GetResult(
                machineGrade - 1,
                metadata.Rarity,
                ExpertJobEquipmentStateResolver.Resolve(source));
        }

        internal static List<DisjointMaterialResult> Calculate(
            ItemCore source,
            ItemMetadata metadata,
            byte machineGrade,
            bool isSelfService)
        {
            var result = new List<DisjointMaterialResult>();
            if (source == null || metadata == null)
                return result;

            var config = DisjointMachineConfigProvider.Config;
            var rule = ResolveRule(source, metadata, machineGrade);
            if (rule == null)
                return result;

            var adjustedSellGold = Math.Max(1, (int)Math.Floor(metadata.SellGold * 1.1));
            var baseCount = Math.Max(
                1,
                (int)Math.Floor(adjustedSellGold * rule.Multiplier / config.BaseConst));
            Add(result, rule.ItemId, baseCount);

            var bigWin = ServerRandom.Next(10000)
                < Math.Max(0, Math.Min(100, rule.BigWinChancePercent)) * 100;
            var table = bigWin ? rule.BigWinTable : rule.AdditionalTable;
            var selections = bigWin ? config.BigWinResults : config.AdditionalResults;
            if (selections.TryGetValue(table, out var rows))
            {
                var selected = ExpertJobSelectionRuleSelector.Select(rows, metadata.Grade);
                if (selected != null)
                {
                    var count = selected.CountDivisor > 0
                        ? (int)Math.Floor(metadata.Grade / selected.CountDivisor)
                        : 1;
                    Add(result, selected.ItemId, Math.Max(1, count));
                }
            }

            foreach (var material in DisjointResultCalculator.CalculateEquipmentSoulResults(metadata))
                Add(result, material.ItemTemplateId, material.Count);

            if (isSelfService
                && metadata.Rarity > 1
                && config.SelfServiceItemId > 0
                && ServerRandom.Next(100) < config.SelfServiceChancePercent)
            {
                Add(result, config.SelfServiceItemId, config.SelfServiceItemCount);
            }

            return result;
        }

        private static void Add(List<DisjointMaterialResult> result, int itemId, int count)
        {
            if (itemId <= 0 || count <= 0)
                return;
            foreach (var item in result)
            {
                if (item.ItemTemplateId != itemId)
                    continue;
                item.Count += count;
                return;
            }
            result.Add(new DisjointMaterialResult
            {
                SlotIndex = -1,
                ItemTemplateId = itemId,
                Count = count,
            });
        }
    }
}
