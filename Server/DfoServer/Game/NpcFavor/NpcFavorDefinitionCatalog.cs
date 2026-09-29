using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.NpcFavor
{
    /// <summary>
    /// 好感度 PVF 目录。NPC、特殊礼物和物品分组均来自同一份 PVF，
    /// 避免在协议层维护易失真的硬编码名单。
    /// </summary>
    internal sealed class NpcFavorDefinitionCatalog
    {
        private readonly Func<string, string> _readText;
        private readonly LstFile _stackableList;
        private readonly LstFile _equipmentList;
        private readonly Dictionary<int, string> _itemGroupCache = new Dictionary<int, string>();
        private readonly Dictionary<int, NpcFavorPointRule> _specialGiftRules;

        private NpcFavorDefinitionCatalog(
            Func<string, string> readText,
            NpcFavorSystemFile system,
            Dictionary<int, NpcFile> npcs,
            LstFile stackableList,
            LstFile equipmentList)
        {
            _readText = readText;
            System = system;
            Npcs = new ReadOnlyDictionary<int, NpcFile>(npcs);
            _stackableList = stackableList;
            _equipmentList = equipmentList;
            _specialGiftRules = new Dictionary<int, NpcFavorPointRule>();
            foreach (var rule in system.PointRules)
            {
                if (rule.ItemId > 0 && !_specialGiftRules.ContainsKey(rule.ItemId))
                    _specialGiftRules[rule.ItemId] = rule;
            }
        }

        internal NpcFavorSystemFile System { get; }

        internal IReadOnlyDictionary<int, NpcFile> Npcs { get; }

        internal static NpcFavorDefinitionCatalog Load(Func<string, string> readText)
        {
            if (readText == null)
                throw new ArgumentNullException(nameof(readText));

            var system = NpcFavorSystemFile.Parse(readText("etc/npcfavorsystem.etc"));
            var npcList = LstFile.Parse(readText("npc/npc.lst"));
            var npcs = new Dictionary<int, NpcFile>();
            foreach (var entry in npcList.Entries)
            {
                if (entry.Id < 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                var npc = NpcFile.Parse(readText(Join("npc", entry.FilePath)));
                if (!HasFavorDefinition(npc, system))
                    continue;
                npcs[entry.Id] = npc;
            }

            return new NpcFavorDefinitionCatalog(
                readText,
                system,
                npcs,
                LstFile.Parse(readText("stackable/stackable.lst")),
                LstFile.Parse(readText("equipment/equipment.lst")));
        }

        internal bool TryGetNpc(int npcId, out NpcFile npc)
            => Npcs.TryGetValue(npcId, out npc);

        internal bool TryGetSpecialGiftRule(
            int itemId,
            out NpcFavorPointRule rule)
            => _specialGiftRules.TryGetValue(itemId, out rule);

        internal bool TryResolveItemGroup(int itemId, out string itemGroupName)
        {
            lock (_itemGroupCache)
            {
                if (_itemGroupCache.TryGetValue(itemId, out itemGroupName))
                    return !string.IsNullOrWhiteSpace(itemGroupName);

                itemGroupName = ResolveItemGroup(itemId);
                _itemGroupCache[itemId] = itemGroupName;
                return !string.IsNullOrWhiteSpace(itemGroupName);
            }
        }

        private string ResolveItemGroup(int itemId)
        {
            var stackable = _stackableList.GetById(itemId);
            if (stackable != null)
            {
                return StackableItemFile.Parse(
                    _readText(Join("stackable", stackable.FilePath)))
                    .ItemGroupName?.Trim();
            }

            var equipment = _equipmentList.GetById(itemId);
            if (equipment != null)
            {
                return EquipmentFile.Parse(
                    _readText(Join("equipment", equipment.FilePath)))
                    .ItemGroupName?.Trim();
            }

            return null;
        }

        private static bool HasFavorDefinition(
            NpcFile npc,
            NpcFavorSystemFile system)
            => npc != null
                && ((npc.DefaultFavor >= 0
                        && npc.MaxGiftPerDay > 0
                        && (npc.PreferredGiftRules.Count > 0
                            || npc.UnpreferredGiftRules.Count > 0))
                    || (npc.FavorableRelationshipVersion >= 2
                        && npc.FavorLevelPoints.Count >= 3
                        && system?.SpecialGiftItemCount > 0
                        && system.SpecialGiftActionLimit > 0
                        && system.PointRules.Count > 0));

        private static string Join(string root, string relativePath)
            => root.TrimEnd('/', '\\') + "/" + relativePath.Replace('\\', '/').TrimStart('/');
    }
}
