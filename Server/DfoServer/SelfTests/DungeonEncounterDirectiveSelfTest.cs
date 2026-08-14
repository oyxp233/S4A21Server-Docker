using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;

namespace DfoServer.SelfTests
{
    internal static class DungeonEncounterDirectiveSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== DUNGEON_ENCOUNTER_DIRECTIVE selftest ===");
            var failures = 0;
            var instance = new DungeonInstance(101, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active);
            var room = instance.GetOrCreateRoom(
                new RoomKey(1, 2, 0),
                roomId => new DungeonInstanceRoom(
                    roomId,
                    new RoomKey(1, 2, 0),
                    new GameWorld.Dungeon.MazeSumInfo
                    {
                        Index = 5001,
                        X = 1,
                        Y = 2,
                        Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>(),
                    },
                    seed: 7),
                out _);
            room.TryActivate();
            run.SetCurrentRoom(room);

            var startSource = Source(run, "room start");
            var startDirective = new DungeonEncounterDirective(
                startSource,
                DungeonEncounterDirectiveKind.Start);
            var started = DungeonEncounterApplicationService.Apply(
                run,
                startDirective);
            Check(
                "room encounter starts through typed directive",
                started.Status == DungeonEncounterApplyStatus.Applied
                && started.Before == DungeonEncounterState.NotStarted
                && started.After == DungeonEncounterState.Active
                && room.EncounterState == DungeonEncounterState.Active,
                ref failures);

            var replayedStart = DungeonEncounterApplicationService.Apply(
                run,
                startDirective);
            Check(
                "same event and directive replays without another transition",
                replayedStart.Status == DungeonEncounterApplyStatus.Replayed
                && replayedStart.Before == DungeonEncounterState.NotStarted
                && replayedStart.After == DungeonEncounterState.Active,
                ref failures);

            var duplicateStart = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    Source(run, "second participant start"),
                    DungeonEncounterDirectiveKind.Start));
            Check(
                "different start event is a no-op while active",
                duplicateStart.Status == DungeonEncounterApplyStatus.NoOp
                && room.EncounterState == DungeonEncounterState.Active,
                ref failures);

            var successSource = Source(run, "room actors cleared");
            var succeeded = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    successSource,
                    DungeonEncounterDirectiveKind.Succeed));
            Check(
                "active encounter reaches terminal succeeded state",
                succeeded.Status == DungeonEncounterApplyStatus.Applied
                && room.EncounterState == DungeonEncounterState.Succeeded,
                ref failures);

            var conflictingFailure = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    Source(run, "late failure"),
                    DungeonEncounterDirectiveKind.Fail));
            Check(
                "terminal encounter cannot roll back or change outcome",
                conflictingFailure.Status
                    == DungeonEncounterApplyStatus.InvalidTransition
                && room.EncounterState == DungeonEncounterState.Succeeded,
                ref failures);

            var staleRoomSource = new DungeonEventEnvelope(
                Guid.NewGuid(),
                run.CaptureIdentity(),
                room.RoomInstanceId + 100,
                1,
                1,
                null,
                null,
                "stale room",
                10);
            var staleRoom = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    staleRoomSource,
                    DungeonEncounterDirectiveKind.Start));
            Check(
                "stale room directive is rejected",
                staleRoom.Status == DungeonEncounterApplyStatus.RejectedRoom,
                ref failures);

            var staleRunSource = new DungeonEventEnvelope(
                Guid.NewGuid(),
                new DungeonRunIdentity(
                    run.PartyDungeonInstanceId,
                    run.RunId,
                    run.RunGeneration + 1),
                room.RoomInstanceId,
                1,
                1,
                null,
                null,
                "stale generation",
                11);
            var staleRun = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    staleRunSource,
                    DungeonEncounterDirectiveKind.Start));
            Check(
                "stale run generation directive is rejected",
                staleRun.Status
                    == DungeonEncounterApplyStatus.RejectedIdentity,
                ref failures);

            var waveStart = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    Source(run, "boss wave start"),
                    DungeonEncounterDirectiveKind.Start,
                    "wave:boss"));
            var waveSuccess = DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    Source(run, "boss wave clear"),
                    DungeonEncounterDirectiveKind.Succeed,
                    "wave:boss"));
            Check(
                "named encounters have independent terminal state",
                waveStart.Applied && waveSuccess.Applied
                && room.EncounterState == DungeonEncounterState.Succeeded,
                ref failures);

            var replay = instance.Diagnostics.ReplayEncounter(
                room.RoomInstanceId);
            var waveReplay = instance.Diagnostics.ReplayEncounter(
                room.RoomInstanceId,
                "wave:boss");
            Check(
                "journal replay reconstructs both encounter outcomes",
                replay.IsComplete
                && replay.IsConsistent
                && replay.FinalState == DungeonEncounterState.Succeeded
                && replay.RejectedRecordCount == 1
                && waveReplay.IsConsistent
                && waveReplay.FinalState == DungeonEncounterState.Succeeded,
                ref failures);

            var snapshot = instance.Diagnostics.Snapshot();
            Check(
                "diagnostic records preserve monotonic order and identity",
                snapshot.Count >= 9
                && snapshot[0].Sequence == 1
                && snapshot[snapshot.Count - 1].Sequence == snapshot.Count
                && snapshot[0].RunIdentity.Equals(run.CaptureIdentity()),
                ref failures);

            var bounded = new DungeonDiagnosticJournal(capacity: 2);
            bounded.Record(
                DungeonDiagnosticRecordKind.ClearIntent,
                startSource,
                "first",
                "seen");
            bounded.Record(
                DungeonDiagnosticRecordKind.ClearIntent,
                startSource,
                "second",
                "seen");
            bounded.Record(
                DungeonDiagnosticRecordKind.ClearCommit,
                successSource,
                "third",
                "committed");
            var boundedSnapshot = bounded.Snapshot();
            Check(
                "diagnostic journal is bounded and reports truncation",
                boundedSnapshot.Count == 2
                && boundedSnapshot[0].Sequence == 2
                && boundedSnapshot[1].Sequence == 3
                && bounded.TruncatedRecordCount == 1,
                ref failures);

            Console.WriteLine(failures == 0
                ? "PASS"
                : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static DungeonEventEnvelope Source(
            DungeonRun run,
            string cause)
        {
            return DungeonEventEnvelope.Create(
                run,
                sourcePlayerId: 1,
                cause: cause);
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine((condition ? "[OK] " : "[FAIL] ") + name);
            if (!condition)
                failures++;
        }
    }
}
