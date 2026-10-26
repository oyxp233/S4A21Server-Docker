using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildDungeonService
    {
        private readonly GuildDungeonRepository _repository;
        private readonly Func<DateTimeOffset> _now;
        internal GuildDungeonService(IGameDatabase database, Func<DateTimeOffset> now = null)
        { _repository = new(database); _now = now ?? (() => DateTimeOffset.UtcNow); }
        internal GuildDungeonSnapshot Read(int characterId) => _repository.Read(characterId,
            GuildDungeonCatalog.Current, GuildDungeonPeriod.At(_now()));
        internal bool TryAdmit(int characterId, DungeonRun run, out string error)
        {
            error = null;
            var catalog = GuildDungeonCatalog.Current;
            if (!catalog.Dungeons.TryGetValue(run.DungeonId, out var definition)) return true;
            var snapshot = Read(characterId);
            if (snapshot == null) { error = "guild membership required"; return false; }
            if (definition.Prerequisites.Any(p => catalog.Dungeons[p].Ratio(snapshot.ClearCounts.GetValueOrDefault(p))
                < catalog.Dungeons[p].ConquestRatio))
            { error = "guild dungeon prerequisites not conquered"; return false; }
            var admission = new GuildDungeonAdmission(snapshot.GuildId, snapshot.Period, snapshot.StyleId);
            if (!run.Instance.Mechanisms.TryAttachGuildDungeon(admission))
            { error = "guild dungeon party membership or period changed"; return false; }
            if (run.Selection.GuildDungeon != null && run.Selection.GuildDungeon != admission)
            { error = "guild dungeon admission changed"; return false; }
            run.Selection.GuildDungeon = admission;
            return true;
        }
        internal GuildDungeonClearResult ApplyClear(int characterId, DungeonRun run)
        {
            if (run.Selection.GuildDungeon == null) return new(false, 0, 0);
            return _repository.RecordClear(characterId, run.DungeonId, run.GetSettlementSourceEventId(),
                run.Selection.GuildDungeon, GuildDungeonCatalog.Current, _now());
        }
    }
}
