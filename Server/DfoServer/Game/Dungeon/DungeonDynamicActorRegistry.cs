using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonDynamicActorPolicy
    {
        internal static readonly DungeonDynamicActorPolicy BloodAltar =
            new DungeonDynamicActorPolicy(
                grantsMonsterExperience: false,
                generatesMonsterDrops: false,
                generatesQuestDrops: true,
                advancesQuestObjectives: true,
                appliesGeneralMechanisms: false,
                countsTowardRoomClear: false,
                tracksKillStatistics: false);

        internal DungeonDynamicActorPolicy(
            bool grantsMonsterExperience,
            bool generatesMonsterDrops,
            bool generatesQuestDrops,
            bool advancesQuestObjectives,
            bool appliesGeneralMechanisms,
            bool countsTowardRoomClear,
            bool tracksKillStatistics)
        {
            GrantsMonsterExperience = grantsMonsterExperience;
            GeneratesMonsterDrops = generatesMonsterDrops;
            GeneratesQuestDrops = generatesQuestDrops;
            AdvancesQuestObjectives = advancesQuestObjectives;
            AppliesGeneralMechanisms = appliesGeneralMechanisms;
            CountsTowardRoomClear = countsTowardRoomClear;
            TracksKillStatistics = tracksKillStatistics;
        }

        internal bool GrantsMonsterExperience { get; }
        internal bool GeneratesMonsterDrops { get; }
        internal bool GeneratesQuestDrops { get; }
        internal bool AdvancesQuestObjectives { get; }
        internal bool AppliesGeneralMechanisms { get; }
        internal bool CountsTowardRoomClear { get; }
        internal bool TracksKillStatistics { get; }
    }

    internal sealed class DungeonDynamicActorDefinition
    {
        internal DungeonDynamicActorDefinition(
            ushort sequenceId,
            int actorCode,
            byte actorType,
            byte actorLevel,
            DungeonRoomIdentity roomIdentity,
            string provider,
            long providerGeneration,
            int waveIdentity,
            DungeonDynamicActorPolicy policy)
        {
            SequenceId = sequenceId;
            ActorCode = actorCode;
            ActorType = actorType;
            ActorLevel = actorLevel;
            RoomIdentity = roomIdentity;
            Provider = provider ?? string.Empty;
            ProviderGeneration = providerGeneration;
            WaveIdentity = waveIdentity;
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        internal ushort SequenceId { get; }
        internal int ActorCode { get; }
        internal byte ActorType { get; }
        internal byte ActorLevel { get; }
        internal DungeonRoomIdentity RoomIdentity { get; }
        internal string Provider { get; }
        internal long ProviderGeneration { get; }
        internal int WaveIdentity { get; }
        internal DungeonDynamicActorPolicy Policy { get; }

        internal bool HasSameIdentity(DungeonDynamicActorDefinition other)
            => other != null
               && SequenceId == other.SequenceId
               && ActorCode == other.ActorCode
               && ActorType == other.ActorType
               && ActorLevel == other.ActorLevel
               && RoomIdentity.Equals(other.RoomIdentity)
               && string.Equals(Provider, other.Provider, StringComparison.Ordinal)
               && ProviderGeneration == other.ProviderGeneration
               && WaveIdentity == other.WaveIdentity
               && ReferenceEquals(Policy, other.Policy);
    }

    internal sealed class DungeonDynamicActorRegistry
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<ushort, DungeonDynamicActorDefinition> _actors =
            new Dictionary<ushort, DungeonDynamicActorDefinition>();

        internal bool TryRegisterBatch(
            IReadOnlyList<DungeonDynamicActorDefinition> definitions,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (definitions == null || definitions.Count == 0)
            {
                failureReason = "dynamic actor batch is empty";
                return false;
            }

            lock (_syncRoot)
            {
                var seen = new HashSet<ushort>();
                foreach (var definition in definitions)
                {
                    if (!IsValid(definition))
                    {
                        failureReason = "dynamic actor definition is invalid";
                        return false;
                    }
                    if (!seen.Add(definition.SequenceId))
                    {
                        failureReason =
                            $"dynamic actor batch repeats sequence {definition.SequenceId}";
                        return false;
                    }
                    if (_actors.TryGetValue(
                            definition.SequenceId,
                            out var existing)
                        && !existing.HasSameIdentity(definition))
                    {
                        failureReason =
                            $"dynamic actor sequence {definition.SequenceId} is already owned";
                        return false;
                    }
                }

                foreach (var definition in definitions)
                    _actors[definition.SequenceId] = definition;
                return true;
            }
        }

        internal bool TryResolve(
            DungeonEventEnvelope source,
            ushort sequenceId,
            out DungeonDynamicActorDefinition definition)
        {
            definition = null;
            if (source == null
                || !source.RoomIdentity.IsValid
                || sequenceId == 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _actors.TryGetValue(sequenceId, out definition)
                    && definition.RoomIdentity.Equals(source.RoomIdentity);
            }
        }

        internal IReadOnlyList<DungeonDynamicActorDefinition> Capture(
            DungeonRoomIdentity roomIdentity,
            string provider,
            long providerGeneration)
        {
            if (!roomIdentity.IsValid || string.IsNullOrWhiteSpace(provider))
                return Array.Empty<DungeonDynamicActorDefinition>();

            lock (_syncRoot)
            {
                var result = new List<DungeonDynamicActorDefinition>();
                foreach (var actor in _actors.Values)
                {
                    if (actor.RoomIdentity.Equals(roomIdentity)
                        && actor.ProviderGeneration == providerGeneration
                        && string.Equals(
                            actor.Provider,
                            provider,
                            StringComparison.Ordinal))
                    {
                        result.Add(actor);
                    }
                }
                return new ReadOnlyCollection<DungeonDynamicActorDefinition>(result);
            }
        }

        private static bool IsValid(DungeonDynamicActorDefinition definition)
            => definition != null
               && definition.SequenceId != 0
               && definition.ActorCode > 0
               && definition.ActorLevel > 0
               && definition.RoomIdentity.IsValid
               && !string.IsNullOrWhiteSpace(definition.Provider)
               && definition.ProviderGeneration > 0
               && definition.WaveIdentity >= 0
               && definition.Policy != null;
    }
}
