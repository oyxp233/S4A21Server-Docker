using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld
{
    internal sealed class DungeonNpcItemDropDefinition
    {
        internal int MapId { get; set; }
        internal int ObjectCode { get; set; }
        internal int X { get; set; }
        internal int Y { get; set; }
        internal string ObjectPath { get; set; }
        internal string ActionPath { get; set; }
    }

    // Resolves scene NPC item drops from map -> passiveobject.lst -> OBJ -> ACT.
    // The ACT behavior tag is the source of truth; object and map ids are only
    // relation keys returned by the PVF lookup.
    internal static class DungeonNpcItemDropData
    {
        private sealed class Resolution
        {
            internal DungeonNpcItemDropDefinition Definition { get; set; }
            internal string Reason { get; set; }
        }

        private static readonly Lazy<LstFile> PassiveObjectList =
            new Lazy<LstFile>(() => Dungeon.LoadLstFile(
                Path.Combine("passiveobject", "passiveobject.lst")));

        private static readonly ConcurrentDictionary<int, Lazy<Resolution>> Cache =
            new ConcurrentDictionary<int, Lazy<Resolution>>();

        private static readonly Regex ActionPathRegex = new Regex(
            @"`(?<quoted>[^`]+\.act)`|(?<plain>[^`\s]+\.act)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool TryResolve(
            int mapId,
            out DungeonNpcItemDropDefinition definition,
            out string reason)
        {
            definition = null;
            reason = string.Empty;
            if (mapId <= 0)
            {
                reason = "invalid map id";
                return false;
            }

            var resolution = Cache.GetOrAdd(
                mapId,
                id => new Lazy<Resolution>(
                    () => Resolve(id),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            definition = resolution.Definition;
            reason = resolution.Reason ?? string.Empty;
            return definition != null;
        }

        private static Resolution Resolve(int mapId)
        {
            try
            {
                var mapList = Dungeon.LoadLstFile(Path.Combine("map", "map.lst"));
                var mapPath = Dungeon.ResolveFilePath(mapList, mapId, "map");
                var map = MapFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("map", mapPath)));

                var matches = new List<DungeonNpcItemDropDefinition>();
                foreach (var passive in map.PassiveObjects)
                {
                    if (passive == null || passive.ObjectCode <= 0)
                        continue;

                    var objectEntry = PassiveObjectList.Value.GetById(passive.ObjectCode);
                    if (objectEntry == null || string.IsNullOrWhiteSpace(objectEntry.FilePath))
                        continue;

                    var objectPath = NormalizePvfPath(
                        "passiveobject/" + objectEntry.FilePath);
                    if (string.IsNullOrWhiteSpace(objectPath))
                        continue;

                    ObjectFile objectFile;
                    try
                    {
                        objectFile = ObjectFile.Parse(PvfArchiveAccessor.ReadText(objectPath));
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var objectDirectory = GetDirectory(objectPath);
                    foreach (var actionReference in EnumerateActionPaths(objectFile))
                    {
                        var actionPath = NormalizePvfPath(
                            objectDirectory + "/" + actionReference);
                        if (string.IsNullOrWhiteSpace(actionPath))
                            continue;

                        ActFile action;
                        try
                        {
                            action = ActFile.Parse(PvfArchiveAccessor.ReadText(actionPath));
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (!action.HasNpcItemDrop)
                            continue;

                        matches.Add(new DungeonNpcItemDropDefinition
                        {
                            MapId = mapId,
                            ObjectCode = passive.ObjectCode,
                            X = passive.X,
                            Y = passive.Y,
                            ObjectPath = objectPath,
                            ActionPath = actionPath,
                        });
                    }
                }

                if (matches.Count == 1)
                    return new Resolution { Definition = matches[0] };

                if (matches.Count == 0)
                {
                    return new Resolution
                    {
                        Reason = "no passive object action contains [NPC ITEM DROP]",
                    };
                }

                return new Resolution
                {
                    Reason = $"ambiguous passive object actions: {matches.Count}",
                };
            }
            catch (Exception ex)
            {
                return new Resolution
                {
                    Reason = $"PVF lookup failed: {ex.Message}",
                };
            }
        }

        private static IEnumerable<string> EnumerateActionPaths(ObjectFile objectFile)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WalkActionNodes(objectFile?.Root, objectFile?.Content, result);
            return result;
        }

        private static void WalkActionNodes(
            ScriptNode node,
            string content,
            ISet<string> result)
        {
            if (node == null)
                return;

            if (node.Tag != null
                && node.Tag.IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (var item in node.DataItems)
                {
                    var raw = item.GetContent(content) ?? string.Empty;
                    foreach (Match match in ActionPathRegex.Matches(raw))
                    {
                        var path = match.Groups["quoted"].Success
                            ? match.Groups["quoted"].Value
                            : match.Groups["plain"].Value;
                        if (!string.IsNullOrWhiteSpace(path))
                            result.Add(path.Trim());
                    }
                }
            }

            foreach (var child in node.Children)
                WalkActionNodes(child, content, result);
        }

        private static string GetDirectory(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : string.Empty;
        }

        private static string NormalizePvfPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var parts = path.Replace('\\', '/').Split('/');
            var normalized = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part) || part == ".")
                    continue;
                if (part == "..")
                {
                    if (normalized.Count == 0)
                        return null;
                    normalized.RemoveAt(normalized.Count - 1);
                    continue;
                }

                normalized.Add(part);
            }

            return normalized.Count == 0
                ? null
                : string.Join("/", normalized);
        }
    }
}
