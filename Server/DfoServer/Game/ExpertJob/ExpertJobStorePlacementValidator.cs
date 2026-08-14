using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobStorePlacementValidator
    {
        internal const byte ErrorUnavailablePoint = 190;
        internal const byte ErrorRestrictedCommercialZone = 82;

        private sealed class PlacementAreaDefinition
        {
            internal int[] VirtualMovableAreas { get; set; }
            internal IReadOnlyList<MapNpcInfo> Npcs { get; set; }
        }

        private readonly object _cacheSync = new object();
        private readonly Dictionary<int, PlacementAreaDefinition> _areaCache =
            new Dictionary<int, PlacementAreaDefinition>();

        internal bool TryValidate(
            byte townId,
            byte areaId,
            short x,
            short y,
            out byte errorCode)
        {
            if (!TryGetAreaDefinition(townId, areaId, out var area))
            {
                errorCode = ErrorUnavailablePoint;
                return false;
            }

            errorCode = Validate(
                area.VirtualMovableAreas,
                area.Npcs,
                x,
                y);
            return errorCode == 0;
        }

        internal static byte Validate(
            int[] virtualMovableAreas,
            IReadOnlyList<MapNpcInfo> npcs,
            int x,
            int y)
        {
            if (!IsInsideAnyMovableArea(x, y, virtualMovableAreas))
                return ErrorUnavailablePoint;

            if (npcs != null)
            {
                foreach (var npc in npcs)
                {
                    if ((long)npc.Y - 150 < y
                        && (long)npc.Y + 150 > y
                        && (long)npc.X - 80 < x
                        && (long)npc.X + 80 > x)
                    {
                        return ErrorRestrictedCommercialZone;
                    }
                }
            }

            return 0;
        }

        private bool TryGetAreaDefinition(
            byte townId,
            byte areaId,
            out PlacementAreaDefinition area)
        {
            var key = (townId << 8) | areaId;
            lock (_cacheSync)
            {
                if (_areaCache.TryGetValue(key, out area))
                    return true;
            }

            PlacementAreaDefinition loadedArea;
            try
            {
                loadedArea = LoadAreaDefinition(townId, areaId);
            }
            catch (Exception ex)
            {
                area = null;
                FileLogger.Log(
                    $"[ExpertJobStorePlacement] PVF load failed town={townId} " +
                    $"area={areaId}: {ex.Message}");
                return false;
            }

            lock (_cacheSync)
            {
                if (_areaCache.TryGetValue(key, out area))
                    return true;

                _areaCache.Add(key, loadedArea);
                area = loadedArea;
                return true;
            }
        }

        private static PlacementAreaDefinition LoadAreaDefinition(byte townId, byte areaId)
        {
            var townList = LstFile.Parse(PvfArchiveAccessor.ReadText("town/town.lst"));
            var townEntry = townList?.GetById(townId);
            if (townEntry == null || string.IsNullOrWhiteSpace(townEntry.FilePath))
                throw new InvalidOperationException($"town {townId} is missing from town.lst");

            var town = TownFile.Parse(PvfArchiveAccessor.ReadText(
                Path.Combine("town", townEntry.FilePath)));
            var townArea = town?.Areas?.FirstOrDefault(candidate => candidate.Id == areaId);
            if (townArea == null || string.IsNullOrWhiteSpace(townArea.MapPath))
                throw new InvalidOperationException($"town {townId} area {areaId} is missing");

            var map = MapFile.Parse(PvfArchiveAccessor.ReadText(
                Path.Combine("map", townArea.MapPath)));
            if (map?.VirtualMovableArea == null
                || map.VirtualMovableArea.Length == 0
                || map.VirtualMovableArea.Length % 4 != 0
                || (map.NpcCount > 0 && map.Npcs.Count != map.NpcCount))
            {
                throw new InvalidOperationException(
                    $"town {townId} area {areaId} has invalid placement data");
            }

            return new PlacementAreaDefinition
            {
                VirtualMovableAreas = (int[])map.VirtualMovableArea.Clone(),
                Npcs = map.Npcs.ToArray(),
            };
        }

        private static bool IsInsideAnyMovableArea(
            int x,
            int y,
            int[] virtualMovableAreas)
        {
            if (virtualMovableAreas == null)
                return false;

            for (var offset = 0;
                offset + 3 < virtualMovableAreas.Length;
                offset += 4)
            {
                var areaX = virtualMovableAreas[offset];
                var areaY = virtualMovableAreas[offset + 1];
                var width = virtualMovableAreas[offset + 2];
                var height = virtualMovableAreas[offset + 3];
                if (width < 0 || height < 0)
                    continue;
                if (x >= areaX
                    && (long)x <= (long)areaX + width
                    && y >= areaY
                    && (long)y <= (long)areaY + height)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
