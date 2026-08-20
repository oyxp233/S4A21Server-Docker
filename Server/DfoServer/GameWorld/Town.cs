using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.GameWorld
{
    public struct CeraRoomInfo
    {
        public byte Town { get; set; }

        public byte Area { get; set; }

        public short X { get; set; }

        public short Y { get; set; }

        public int WorldMapAreaId { get; set; }
    }

    public class Town
    {
        private static readonly object DungeonGateReturnCacheSync = new object();
        private static readonly Dictionary<long, CeraRoomInfo?> DungeonGateReturnCache =
            new Dictionary<long, CeraRoomInfo?>();

        public static CeraRoomInfo GetCeraRoomInfo(int townId)
        {
            var roomInfo = new CeraRoomInfo();

            var twnlst = LstFile.Parse(PvfArchiveAccessor.ReadText("town/town.lst"));
            if (twnlst == null)
                throw new Exception("未能成功解析城镇LST文件 town/town.lst");

            var entry = twnlst.GetById(townId);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                throw new Exception($"未找到城镇编号{townId}");

            var twnFile = TownFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("town", entry.FilePath)));
            if (twnFile.Areas == null || twnFile.Areas.Count == 0)
                throw new Exception("未解析到城镇区域信息");

            foreach (var item in twnFile.Areas)
            {
                if (string.Equals(item.AreaType, "gate", StringComparison.OrdinalIgnoreCase))
                {
                    roomInfo.Town = (byte)townId;
                    roomInfo.Area = (byte)item.Id;
                    roomInfo.X = (short)item.LinkedId;
                    roomInfo.Y = (short)item.LinkedId2;
                    break;
                }
            }

            return roomInfo;
        }

        public static bool IsCeraRoom(int townId, int areaId)
        {
            try
            {
                var room = GetCeraRoomInfo(townId);
                return room.Town == townId && room.Area == areaId;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetDungeonGateReturnInfo(
            int townId,
            int areaId,
            out CeraRoomInfo roomInfo)
        {
            var key = ((long)townId << 32) | (uint)areaId;
            lock (DungeonGateReturnCacheSync)
            {
                if (DungeonGateReturnCache.TryGetValue(key, out var cached))
                {
                    roomInfo = cached.GetValueOrDefault();
                    return cached.HasValue;
                }
            }

            CeraRoomInfo? resolved = null;
            try
            {
                var twnlst = LstFile.Parse(PvfArchiveAccessor.ReadText("town/town.lst"));
                var entry = twnlst?.GetById(townId);
                if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                {
                    var town = TownFile.Parse(PvfArchiveAccessor.ReadText(
                        Path.Combine("town", entry.FilePath)));
                    var area = town?.Areas?.FirstOrDefault(candidate =>
                        candidate.Id == areaId
                        && string.Equals(
                            candidate.AreaType,
                            "dungeon gate",
                            StringComparison.OrdinalIgnoreCase));
                    if (area != null && !string.IsNullOrEmpty(area.MapPath))
                    {
                        var map = MapFile.Parse(PvfArchiveAccessor.ReadText(
                            Path.Combine("map", area.MapPath)));
                        if (TryFindDungeonGateReturnPosition(
                                map?.TownMovableArea,
                                map?.VirtualMovableArea,
                                out var x,
                                out var y))
                        {
                            resolved = new CeraRoomInfo
                            {
                                Town = checked((byte)townId),
                                Area = checked((byte)areaId),
                                X = x,
                                Y = y,
                                WorldMapAreaId = area.LinkedId,
                            };
                        }
                    }
                }
            }
            catch
            {
                resolved = null;
            }

            lock (DungeonGateReturnCacheSync)
                DungeonGateReturnCache[key] = resolved;
            roomInfo = resolved.GetValueOrDefault();
            return resolved.HasValue;
        }

        internal static bool TryFindDungeonGateReturnPosition(
            int[] townMovableAreas,
            int[] virtualMovableAreas,
            out short resultX,
            out short resultY)
        {
            resultX = 0;
            resultY = 0;
            if (townMovableAreas == null
                || virtualMovableAreas == null
                || townMovableAreas.Length < 6
                || virtualMovableAreas.Length < 4)
            {
                return false;
            }

            const int margin = 8;
            long bestDistance = long.MaxValue;
            int bestX = 0;
            int bestY = 0;
            var found = false;

            for (var gateOffset = 0;
                gateOffset + 5 < townMovableAreas.Length;
                gateOffset += 6)
            {
                // -1/-1 is the local dungeon-selection gate. Other rows link
                // ordinary town maps and must not be used as dungeon return points.
                if (townMovableAreas[gateOffset + 4] != -1
                    || townMovableAreas[gateOffset + 5] != -1)
                {
                    continue;
                }

                var gateX = townMovableAreas[gateOffset];
                var gateY = townMovableAreas[gateOffset + 1];
                var gateWidth = Math.Max(0, townMovableAreas[gateOffset + 2]);
                var gateHeight = Math.Max(0, townMovableAreas[gateOffset + 3]);
                var centerX = gateX + gateWidth / 2;
                var centerY = gateY + gateHeight / 2;
                var candidates = new[]
                {
                    new[] { gateX - margin, centerY },
                    new[] { gateX + gateWidth + margin, centerY },
                    new[] { centerX, gateY - margin },
                    new[] { centerX, gateY + gateHeight + margin },
                };

                foreach (var candidate in candidates)
                {
                    if (!IsInsideAnyMovableArea(
                            candidate[0],
                            candidate[1],
                            virtualMovableAreas))
                    {
                        continue;
                    }

                    var dx = candidate[0] - centerX;
                    var dy = candidate[1] - centerY;
                    var distance = (long)dx * dx + (long)dy * dy;
                    if (distance >= bestDistance)
                        continue;
                    bestDistance = distance;
                    bestX = candidate[0];
                    bestY = candidate[1];
                    found = true;
                }
            }

            if (!found
                || bestX < short.MinValue
                || bestX > short.MaxValue
                || bestY < short.MinValue
                || bestY > short.MaxValue)
            {
                return false;
            }

            resultX = (short)bestX;
            resultY = (short)bestY;
            return true;
        }

        private static bool IsInsideAnyMovableArea(
            int x,
            int y,
            int[] virtualMovableAreas)
        {
            for (var offset = 0;
                offset + 3 < virtualMovableAreas.Length;
                offset += 4)
            {
                var areaX = virtualMovableAreas[offset];
                var areaY = virtualMovableAreas[offset + 1];
                var width = Math.Max(0, virtualMovableAreas[offset + 2]);
                var height = Math.Max(0, virtualMovableAreas[offset + 3]);
                if (x >= areaX
                    && x <= areaX + width
                    && y >= areaY
                    && y <= areaY + height)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
