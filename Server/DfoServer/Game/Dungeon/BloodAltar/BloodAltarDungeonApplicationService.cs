using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon.BloodAltar
{
    internal sealed class BloodAltarDungeonApplicationService
    {
        internal bool TryPrepareRun(
            DungeonRun run,
            out BloodAltarDungeonDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            if (run == null)
            {
                failureReason = "run is missing";
                return false;
            }
            if (!BloodAltarDungeonDefinitionCatalog.IsBloodAltarDungeon(
                    run.DungeonId))
            {
                return true;
            }
            if (!BloodAltarDungeonDefinitionCatalog.TryResolveDungeon(
                    run.DungeonId,
                    out definition,
                    out failureReason))
            {
                return false;
            }

            var existing = run.Instance.Mechanisms.BloodAltar;
            if (existing != null)
            {
                if (existing.Definition.DungeonId != definition.DungeonId)
                {
                    failureReason =
                        "dungeon instance owns another blood altar definition";
                    return false;
                }
                return true;
            }

            var runtime = new BloodAltarDungeonRuntime(definition);
            if (!run.Instance.Mechanisms.TryAttachBloodAltar(runtime))
            {
                failureReason = "blood altar runtime attachment was rejected";
                return false;
            }
            return true;
        }

        internal bool TryBindMap(
            DungeonRun run,
            int mapId,
            DungeonParticipantRoomIdentity participantRoom,
            out bool changed,
            out string failureReason)
        {
            changed = false;
            failureReason = string.Empty;
            var runtime = GetRuntime(run);
            if (runtime == null)
                return true;
            if (run == null
                || !run.Matches(participantRoom)
                || mapId <= 0)
            {
                failureReason = "blood altar room identity is stale";
                return false;
            }
            if (!BloodAltarDungeonDefinitionCatalog.TryResolveMap(
                    runtime.Definition,
                    mapId,
                    out var map,
                    out failureReason))
            {
                return false;
            }
            if (!runtime.TryBindMap(map, participantRoom.Room, out changed))
            {
                failureReason = "blood altar map transition is not legal";
                return false;
            }
            return true;
        }

        internal bool TryBeginNextRound(
            DungeonRun run,
            DateTime startedUtc,
            out BloodAltarRoundSchedule schedule)
        {
            schedule = null;
            return GetRuntime(run)?.TryBeginNextRound(
                startedUtc,
                out schedule) == true;
        }

        internal bool TryGetNextWaveDeadline(
            DungeonRun run,
            long scheduleGeneration,
            out int waveIndex,
            out DateTime deadlineUtc)
        {
            var runtime = GetRuntime(run);
            if (runtime != null)
            {
                return runtime.TryGetNextWaveDeadline(
                    scheduleGeneration,
                    out waveIndex,
                    out deadlineUtc);
            }
            waveIndex = -1;
            deadlineUtc = DateTime.MinValue;
            return false;
        }

        internal bool TryMaterializeWave(
            DungeonRun run,
            long scheduleGeneration,
            int waveIndex,
            out BloodAltarWave wave,
            out bool schedulingComplete,
            out string failureReason)
        {
            wave = null;
            schedulingComplete = false;
            failureReason = string.Empty;
            var runtime = GetRuntime(run);
            if (runtime == null
                || !runtime.TryReserveScheduledWave(
                    scheduleGeneration,
                    waveIndex,
                    out var reservation,
                    out wave))
            {
                failureReason = "blood altar wave reservation was rejected";
                return false;
            }

            var actors = new List<DungeonDynamicActorDefinition>(
                wave.Monsters.Count);
            foreach (var spawn in wave.Monsters)
            {
                actors.Add(new DungeonDynamicActorDefinition(
                    spawn.SequenceId,
                    spawn.MonsterCode,
                    spawn.MonsterType,
                    spawn.Level,
                    runtime.CurrentRoomIdentity,
                    BloodAltarDungeonRuntime.DynamicActorProvider,
                    spawn.ProviderGeneration,
                    spawn.WaveIdentity,
                    DungeonDynamicActorPolicy.BloodAltar));
            }

            if (!run.Instance.Mechanisms.DynamicActors.TryRegisterBatch(
                    actors,
                    out failureReason))
            {
                runtime.FailScheduledWave(reservation);
                wave = null;
                return false;
            }
            if (!runtime.TryCommitScheduledWave(
                    reservation,
                    out schedulingComplete))
            {
                runtime.FailScheduledWave(reservation);
                wave = null;
                failureReason = "blood altar wave commit was rejected";
                return false;
            }
            return true;
        }

        internal bool CanAcceptActorDeath(
            DungeonRun run,
            DungeonDynamicActorDefinition actor)
        {
            var runtime = GetRuntime(run);
            return runtime != null
                && actor != null
                && string.Equals(
                    actor.Provider,
                    BloodAltarDungeonRuntime.DynamicActorProvider,
                    StringComparison.Ordinal)
                && runtime.CanAcceptActorDeath(
                    actor.RoomIdentity,
                    actor.SequenceId,
                    actor.ProviderGeneration);
        }

        internal bool TryApplyActorDeath(
            DungeonRun run,
            DungeonDynamicActorDefinition actor,
            out BloodAltarProgress progress,
            out IReadOnlyList<ushort> releasedSequences)
        {
            var runtime = GetRuntime(run);
            if (runtime != null)
            {
                return runtime.TryApplyActorDeath(
                    actor,
                    out progress,
                    out releasedSequences);
            }
            progress = BloodAltarProgress.None;
            releasedSequences = Array.Empty<ushort>();
            return false;
        }

        internal bool TryAdvanceAfterScheduling(
            DungeonRun run,
            out BloodAltarProgress progress)
        {
            var runtime = GetRuntime(run);
            if (runtime != null)
                return runtime.TryAdvanceAfterScheduling(out progress);
            progress = BloodAltarProgress.None;
            return false;
        }

        internal bool TryResolveUltimateDifficulty(
            DungeonRun run,
            byte difficulty,
            int expectedPromptVersion,
            out int roundNumber)
        {
            var runtime = GetRuntime(run);
            if (runtime != null)
            {
                return runtime.TryResolveUltimateDifficulty(
                    difficulty,
                    expectedPromptVersion,
                    out roundNumber);
            }
            roundNumber = 0;
            return false;
        }

        internal bool TryCreateClearIntent(
            DungeonRun run,
            DungeonEventEnvelope source,
            out DungeonClearIntent intent)
        {
            intent = null;
            var runtime = GetRuntime(run);
            if (runtime == null
                || !runtime.IsDungeonComplete
                || run == null
                || source == null
                || !run.Matches(source.RunIdentity)
                || run.CurrentRoomInstanceId <= 0)
            {
                return false;
            }

            intent = new DungeonClearIntent(
                source,
                "blood altar all rounds complete",
                bossCode: 0,
                presentationKind: DungeonClearPresentationKind.BloodAltar);
            return true;
        }

        internal bool BlocksMapMove(DungeonRun run)
            => GetRuntime(run)?.BlocksMapMove == true;

        internal bool IsBloodAltar(DungeonRun run)
            => GetRuntime(run) != null;

        internal BloodAltarDungeonRuntime GetRuntime(DungeonRun run)
            => run?.Instance?.Mechanisms?.BloodAltar;
    }
}
