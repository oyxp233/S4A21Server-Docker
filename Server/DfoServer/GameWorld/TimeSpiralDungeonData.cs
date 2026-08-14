using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld
{
    internal static class TimeSpiralDungeonData
    {
        private const byte SpecialPassiveObjectActorType = 9;
        private const string HiddenBossPath = "NewMonsters/Timespiral/mv_771_boss/";

        internal sealed class PassiveObjectMatch
        {
            internal string MapSource { get; set; }
            internal string ObjectPath { get; set; }
        }

        internal sealed class TeleportTarget
        {
            internal int TrapMapId { get; set; }
            internal int X { get; set; }
            internal int Y { get; set; }
            internal int Weight { get; set; }
            internal int Flag { get; set; }
        }

        internal sealed class TrapBuff
        {
            internal int Index { get; set; }
            internal int Weight { get; set; }
            internal int PhysicalAttack { get; set; }
            internal int MagicalAttack { get; set; }
            internal int MoveSpeed { get; set; }
            internal int AttackSpeed { get; set; }
            internal int CastSpeed { get; set; }
            internal int BuffTimeMs { get; set; }

            internal bool HasStats =>
                PhysicalAttack != 0
                || MagicalAttack != 0
                || MoveSpeed != 0
                || AttackSpeed != 0
                || CastSpeed != 0;
        }

        internal sealed class HiddenBossCandidate
        {
            internal int Code { get; set; }
            internal ushort SequenceId { get; set; }
            internal int LocalIndex { get; set; }
            internal byte Type { get; set; }
            internal string MonsterPath { get; set; }
        }

        private static readonly Lazy<Dictionary<int, List<TeleportTarget>>> TeleportTargets =
            new Lazy<Dictionary<int, List<TeleportTarget>>>(LoadTeleportTargets);
        private static readonly Lazy<List<TrapBuff>> TrapBuffs =
            new Lazy<List<TrapBuff>>(LoadTrapBuffs);
        private static readonly Lazy<Dictionary<int, string>> MonsterPaths =
            new Lazy<Dictionary<int, string>>(
                () => LoadLstPaths(Path.Combine("monster", "monster.lst")));

        internal static bool IsDungeon(int dungeonId)
        {
            try
            {
                return Dungeon.GetDungeonFile(dungeonId)?.Root?.GetChild("time spiral") != null;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryPickTeleportTarget(int trapMapId, out TeleportTarget target)
        {
            target = null;
            if (trapMapId <= 0
                || !TeleportTargets.Value.TryGetValue(trapMapId, out var targets)
                || targets == null
                || targets.Count == 0)
            {
                return false;
            }

            var totalWeight = 0;
            foreach (var candidate in targets)
                if (candidate.Weight > 0)
                    totalWeight += candidate.Weight;

            if (totalWeight <= 0)
            {
                target = targets[0];
                return true;
            }

            var roll = ServerRandom.Next(totalWeight);
            foreach (var candidate in targets)
            {
                if (candidate.Weight <= 0)
                    continue;
                if (roll < candidate.Weight)
                {
                    target = candidate;
                    return true;
                }

                roll -= candidate.Weight;
            }

            target = targets[0];
            return true;
        }

        internal static bool TryGetConditionGatePassiveObject(
            int mapId,
            int objectCode,
            out PassiveObjectMatch match)
        {
            match = null;
            if (mapId <= 0 || objectCode <= 0)
                return false;

            try
            {
                var mapFile = LoadMapFile(mapId);
                var source = FindPassiveObjectSource(mapFile, objectCode);
                if (source == null)
                    return false;

                var objectLst = Dungeon.LoadLstFile(
                    Path.Combine("passiveobject", "passiveobject.lst"));
                var objectPath = objectLst.GetById(objectCode)?.FilePath ?? string.Empty;
                if (!IsConditionGatePath(objectPath.Replace('\\', '/')))
                    return false;

                match = new PassiveObjectMatch
                {
                    MapSource = source,
                    ObjectPath = objectPath,
                };
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TimeSpiral] condition gate lookup failed: " +
                    $"map={mapId} object={objectCode} error={ex.Message}");
                return false;
            }
        }

        internal static bool TryFindHiddenBossCandidate(
            Dungeon.MazeSumInfo maze,
            int firstSequenceId,
            out HiddenBossCandidate candidate)
        {
            candidate = null;
            if (maze.Monsters == null || maze.Monsters.Count == 0)
                return false;

            for (var i = maze.Monsters.Count - 1; i >= 0; i--)
            {
                var monster = maze.Monsters[i];
                if (monster.Code <= 0
                    || monster.Type == SpecialPassiveObjectActorType
                    || !IsHiddenBossMonster(monster.Code, out var monsterPath))
                {
                    continue;
                }

                var sequenceId = firstSequenceId + i;
                if (sequenceId < 0 || sequenceId > ushort.MaxValue)
                    continue;

                candidate = new HiddenBossCandidate
                {
                    Code = monster.Code,
                    SequenceId = (ushort)sequenceId,
                    LocalIndex = i,
                    Type = monster.Type,
                    MonsterPath = monsterPath,
                };
                return true;
            }

            return false;
        }

        internal static bool IsHiddenBossMonster(int monsterCode)
            => IsHiddenBossMonster(monsterCode, out _);

        internal static bool TryPickTrapBuff(
            Func<int, int> nextRandom,
            out TrapBuff buff,
            out int roll,
            out int totalWeight)
        {
            buff = null;
            roll = 0;
            totalWeight = 0;

            var buffs = TrapBuffs.Value;
            if (buffs == null || buffs.Count == 0)
                return false;

            foreach (var candidate in buffs)
                if (candidate.Weight > 0)
                    totalWeight += candidate.Weight;

            if (totalWeight <= 0)
            {
                buff = buffs[0];
                return true;
            }

            roll = nextRandom != null
                ? nextRandom(totalWeight)
                : ServerRandom.Next(totalWeight);
            if (roll < 0)
                roll = 0;
            if (roll >= totalWeight)
                roll %= totalWeight;

            var cursor = roll;
            foreach (var candidate in buffs)
            {
                if (candidate.Weight <= 0)
                    continue;
                if (cursor < candidate.Weight)
                {
                    buff = candidate;
                    return true;
                }

                cursor -= candidate.Weight;
            }

            buff = buffs[buffs.Count - 1];
            return true;
        }

        private static bool IsHiddenBossMonster(int monsterCode, out string monsterPath)
        {
            monsterPath = string.Empty;
            if (monsterCode <= 0
                || !MonsterPaths.Value.TryGetValue(monsterCode, out monsterPath)
                || string.IsNullOrWhiteSpace(monsterPath))
            {
                return false;
            }

            return monsterPath.Replace('\\', '/').IndexOf(
                HiddenBossPath,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<int, List<TeleportTarget>> LoadTeleportTargets()
        {
            var result = new Dictionary<int, List<TeleportTarget>>();
            var text = ReadTimeSpiralEtc();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            try
            {
                var root = new ScriptParser().Parse(text);
                foreach (var trapNode in root.GetChildren("teleport trap"))
                {
                    if (!TryParseFirstInt(
                            trapNode.GetFirstDataContent(text),
                            out var trapMapId)
                        || trapMapId <= 0)
                    {
                        continue;
                    }

                    var moveList = trapNode.GetChild("move list");
                    if (moveList == null)
                        continue;

                    var numbers = Regex.Matches(
                        moveList.GetFirstDataContent(text) ?? string.Empty,
                        @"[+-]?\d+");
                    var targets = new List<TeleportTarget>();
                    for (var i = 0; i + 3 < numbers.Count; i += 4)
                    {
                        if (!int.TryParse(numbers[i].Value, out var x)
                            || !int.TryParse(numbers[i + 1].Value, out var y)
                            || !int.TryParse(numbers[i + 2].Value, out var weight)
                            || !int.TryParse(numbers[i + 3].Value, out var flag))
                        {
                            continue;
                        }

                        targets.Add(new TeleportTarget
                        {
                            TrapMapId = trapMapId,
                            X = x,
                            Y = y,
                            Weight = weight,
                            Flag = flag,
                        });
                    }

                    if (targets.Count > 0)
                        result[trapMapId] = targets;
                }

                FileLogger.Log(
                    $"[TimeSpiral] teleport config loaded: traps={result.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TimeSpiral] teleport config parse failed: {ex.Message}");
            }

            return result;
        }

        private static List<TrapBuff> LoadTrapBuffs()
        {
            var result = new List<TrapBuff>();
            var text = ReadTimeSpiralEtc();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            try
            {
                var root = new ScriptParser().Parse(text);
                var weights = ParseBuffWeights(root.GetChild("trap buff rate"), text);
                foreach (var buffNode in root.GetChildren("trap buff"))
                {
                    if (!TryParseFirstInt(
                            buffNode.GetFirstDataContent(text),
                            out var index))
                    {
                        continue;
                    }

                    var buff = new TrapBuff
                    {
                        Index = index,
                        Weight = weights.TryGetValue(index, out var weight)
                            ? weight
                            : 0,
                        PhysicalAttack = ReadBuffInt(
                            buffNode, text, "physical attack"),
                        MagicalAttack = ReadBuffInt(
                            buffNode, text, "magical attack"),
                        MoveSpeed = ReadBuffInt(
                            buffNode, text, "move speed"),
                        AttackSpeed = ReadBuffInt(
                            buffNode, text, "attack speed"),
                        CastSpeed = ReadBuffInt(
                            buffNode, text, "cast speed"),
                        BuffTimeMs = ReadBuffInt(
                            buffNode, text, "buff time"),
                    };

                    if (buff.Weight > 0 && buff.HasStats)
                        result.Add(buff);
                }

                FileLogger.Log(
                    $"[TimeSpiral] trap buff config loaded: buffs={result.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TimeSpiral] trap buff config parse failed: {ex.Message}");
            }

            return result;
        }

        private static string ReadTimeSpiralEtc()
        {
            try
            {
                return PvfArchiveAccessor.ReadText("etc/global_timespiral.etc");
            }
            catch
            {
                try
                {
                    return PvfArchiveAccessor.ReadText("Etc/global_timespiral.etc");
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[TimeSpiral] config missing: " +
                        $"etc/global_timespiral.etc error={ex.Message}");
                    return string.Empty;
                }
            }
        }

        private static Dictionary<int, int> ParseBuffWeights(
            ScriptNode node,
            string text)
        {
            var result = new Dictionary<int, int>();
            if (node == null)
                return result;

            var numbers = Regex.Matches(
                node.GetFirstDataContent(text) ?? string.Empty,
                @"[+-]?\d+");
            for (var i = 0; i + 1 < numbers.Count; i += 2)
            {
                if (int.TryParse(numbers[i].Value, out var index)
                    && int.TryParse(numbers[i + 1].Value, out var weight)
                    && weight > 0)
                {
                    result[index] = weight;
                }
            }

            return result;
        }

        private static int ReadBuffInt(
            ScriptNode node,
            string text,
            string tag)
        {
            var child = node?.GetChild(tag);
            if (child != null
                && TryParseFirstInt(child.GetFirstDataContent(text), out var value))
            {
                return value;
            }

            if (node == null || string.IsNullOrEmpty(text))
                return 0;

            var content = node.GetContent(text);
            var match = Regex.Match(
                content ?? string.Empty,
                @"\[" + Regex.Escape(tag) + @"\]\s*(?<value>[+-]?\d+)",
                RegexOptions.IgnoreCase);
            return match.Success
                && int.TryParse(match.Groups["value"].Value, out value)
                    ? value
                    : 0;
        }

        private static MapFile LoadMapFile(int mapId)
        {
            var mapLst = Dungeon.LoadLstFile(Path.Combine("map", "map.lst"));
            var path = Dungeon.ResolveFilePath(mapLst, mapId, "地图");
            return MapFile.Parse(
                PvfArchiveAccessor.ReadText(Path.Combine("map", path)));
        }

        private static string FindPassiveObjectSource(
            MapFile mapFile,
            int objectCode)
        {
            if (mapFile?.PassiveObjects != null)
            {
                foreach (var obj in mapFile.PassiveObjects)
                    if (obj != null && obj.ObjectCode == objectCode)
                        return "passive object";
            }

            if (mapFile?.SpecialPassiveObjects != null)
            {
                foreach (var obj in mapFile.SpecialPassiveObjects)
                    if (obj != null && obj.ObjectCode == objectCode)
                        return "special passive object";
            }

            return null;
        }

        private static bool IsConditionGatePath(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
                return false;

            return normalizedPath.IndexOf(
                       "MapObject/PathGate/Timespiral/",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.EndsWith(
                    "Actionobject/map/Timespiral/Timespiral_Trap.obj",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, string> LoadLstPaths(string path)
        {
            var result = new Dictionary<int, string>();
            try
            {
                foreach (var entry in Dungeon.LoadLstFile(path).Entries)
                {
                    if (entry != null
                        && entry.Id > 0
                        && !string.IsNullOrWhiteSpace(entry.FilePath))
                    {
                        result[entry.Id] = entry.FilePath;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TimeSpiral] LST load failed: path={path} error={ex.Message}");
            }

            return result;
        }

        private static bool TryParseFirstInt(string value, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var match = Regex.Match(value, @"[+-]?\d+");
            return match.Success && int.TryParse(match.Value, out result);
        }
    }
}
