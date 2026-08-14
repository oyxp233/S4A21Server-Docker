using PvfLib;
using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    internal static class EquipmentDropPoolProvider
    {
        private static readonly object LockObj = new object();
        private static Dictionary<long, List<(int Id, int Weight)>> _pool;
        private static Dictionary<long, List<(int Id, int Weight)>> _normalPool;
        private static Dictionary<long, List<(int Id, int Weight)>> _avatarPool;
        private static bool _loaded;

        internal static Dictionary<long, List<(int Id, int Weight)>> GetPool()
        {
            EnsureLoaded();
            return _pool;
        }

        internal static Dictionary<long, List<(int Id, int Weight)>>
            GetClearRewardPool(bool avatar)
        {
            EnsureLoaded();
            return avatar ? _avatarPool : _normalPool;
        }

        internal static void WarmUp()
        {
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (LockObj)
            {
                if (_loaded) return;
                _pool = LoadEquipmentPool();
                _loaded = true;
            }
        }

        private static Dictionary<long, List<(int Id, int Weight)>> LoadEquipmentPool()
        {
            var pool = new Dictionary<long, List<(int Id, int Weight)>>();
            _normalPool = new Dictionary<long, List<(int Id, int Weight)>>();
            _avatarPool = new Dictionary<long, List<(int Id, int Weight)>>();
            try
            {
                var equipmentDefinitions = EquipmentDefinitionCatalog.GetAll();
                if (equipmentDefinitions.Count == 0)
                {
                    FileLogger.Log("[EquipmentDropPoolProvider] equipment.lst empty/not found");
                    return pool;
                }

                var added = 0;
                var errors = 0;
                for (var i = 0; i < equipmentDefinitions.Count; i++)
                {
                    var equipment = equipmentDefinitions[i];
                    if (equipment == null)
                        continue;

                    if (TryAddEquipment(
                            pool,
                            _normalPool,
                            _avatarPool,
                            equipment))
                        added++;
                }

                FileLogger.Log(
                    $"[EquipmentDropPoolProvider] equipment pool from .equ: " +
                    $"items={added} errors={errors} buckets={pool.Count} " +
                    $"normalBuckets={_normalPool.Count} avatarBuckets={_avatarPool.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[EquipmentDropPoolProvider] equipment pool parse error: {ex.Message}");
            }

            return pool;
        }

        private static bool TryAddEquipment(
            Dictionary<long, List<(int Id, int Weight)>> pool,
            Dictionary<long, List<(int Id, int Weight)>> normalPool,
            Dictionary<long, List<(int Id, int Weight)>> avatarPool,
            EquipmentDefinition equipment)
        {
            if (equipment == null || equipment.ItemTemplateId <= 0)
                return false;

            var rarity = equipment.Rarity;
            var grade = equipment.Grade;
            var creationRate = equipment.CreationRate;

            if (creationRate <= 0 || grade <= 0 || rarity < 0 || rarity > 5)
                return false;

            var key = (long)grade * 10 + rarity;
            AddToPool(pool, key, equipment.ItemTemplateId, creationRate);
            var categoryPool = IsAvatar(equipment)
                ? avatarPool
                : normalPool;
            AddToPool(categoryPool, key, equipment.ItemTemplateId, creationRate);
            return true;
        }

        private static void AddToPool(
            Dictionary<long, List<(int Id, int Weight)>> pool,
            long key,
            int itemId,
            int weight)
        {
            if (!pool.TryGetValue(key, out var list))
            {
                list = new List<(int Id, int Weight)>();
                pool[key] = list;
            }
            list.Add((itemId, weight));
        }

        private static bool IsAvatar(EquipmentDefinition equipment)
        {
            var normalizedPath = "/" + (equipment.FilePath ?? string.Empty)
                .Replace('\\', '/')
                .Trim('/');
            return normalizedPath.IndexOf(
                       "/avatar/",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.IndexOf(
                       "/at_avatar/",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || (equipment.EquipmentType?.IndexOf(
                        "avatar",
                        StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }
    }
}
