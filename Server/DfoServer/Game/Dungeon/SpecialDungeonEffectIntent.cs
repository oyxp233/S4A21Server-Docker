using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum SpecialDungeonEffectKind
    {
        GaugeChanged,
        BuffAddedAndActivated,
        BuffsCleared,
        BossEntranceMinimap,
        PassGate,
        StrongWarlordSelected,
        SummonMonsterResponse,
        CommandSuccessAck,
        CancelMechanismTimer,
        ResetTimeCrackGauge,
        RecordSeaChaseBuffs,
    }

    internal sealed class SpecialDungeonEffectIntent
    {
        internal SpecialDungeonEffectKind Kind { get; init; }
        internal string Reason { get; init; }
        internal int Value { get; init; }
        internal ushort WireType { get; init; }
        internal IReadOnlyList<int> BuffIds { get; init; }
        internal IReadOnlyList<int> ActiveBuffIds { get; init; }
        internal IReadOnlyList<(byte X, byte Y, int MonsterCode)> MinimapEntries
        {
            get;
            init;
        }
        internal int StateId { get; init; }
        internal int MonsterCode { get; init; }
        internal ushort MonsterLevel { get; init; }
        internal int MapId { get; init; }
        internal int LocalIndex { get; init; }

        internal SpecialDungeonEffectIntent Freeze()
            => new SpecialDungeonEffectIntent
            {
                Kind = Kind,
                Reason = Reason,
                Value = Value,
                WireType = WireType,
                BuffIds = Copy(BuffIds),
                ActiveBuffIds = Copy(ActiveBuffIds),
                MinimapEntries = Copy(MinimapEntries),
                StateId = StateId,
                MonsterCode = MonsterCode,
                MonsterLevel = MonsterLevel,
                MapId = MapId,
                LocalIndex = LocalIndex,
            };

        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<T>();

            var copy = new T[values.Count];
            for (var index = 0; index < values.Count; index++)
                copy[index] = values[index];
            return Array.AsReadOnly(copy);
        }
    }
}
