using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Tournament;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.SelfTests
{
    public static class TournamentSettlementSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== TOURNAMENT_SETTLEMENT selftest ===");
            var failures = 0;
            var run = CreateCompletedTournamentRun();
            var application = new TournamentDungeonApplicationService(_ => true);

            var created = application.TryCreateClearIntent(
                run,
                sourcePlayerId: 1,
                out var intent);
            Check(
                "tournament completion emits a typed dedicated presentation",
                created
                && intent != null
                && intent.PresentationKind
                    == DungeonClearPresentationKind.Tournament,
                ref failures);
            Check(
                "tournament presentation cannot enter standard cards or EXP",
                !DungeonClearPresentationPolicy.UsesStandardResultProjection(
                    intent.PresentationKind)
                && !DungeonClearPresentationPolicy
                    .UsesCommonExperienceAuthority(intent.PresentationKind)
                && DungeonClearPresentationPolicy.CompletesAtClearCommit(
                    intent.PresentationKind),
                ref failures);
            Check(
                "ordinary presentation retains the common settlement authority",
                DungeonClearPresentationPolicy.UsesStandardResultProjection(
                    DungeonClearPresentationKind.Standard)
                && DungeonClearPresentationPolicy
                    .UsesCommonExperienceAuthority(
                        DungeonClearPresentationKind.Standard)
                && !DungeonClearPresentationPolicy.CompletesAtClearCommit(
                    DungeonClearPresentationKind.Standard),
                ref failures);

            Console.WriteLine(failures == 0
                ? "TOURNAMENT_SETTLEMENT selftest: PASS"
                : $"TOURNAMENT_SETTLEMENT selftest: FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static DungeonRun CreateCompletedTournamentRun()
        {
            var definition = new TournamentDungeonDefinition(
                dungeonId: 1,
                mapId: 1,
                basicLevel: 1,
                partyLimit: 1,
                coinLimit: 0,
                roundFatigue: 0,
                clearRewardGoldRate: 1.0f,
                experienceByRound: new Dictionary<int, uint>(),
                resultCards:
                    new Dictionary<int, TournamentResultCardDefinition>(),
                rewardItemRates:
                    Array.Empty<TournamentRewardItemRateDefinition>(),
                candidates: Array.Empty<TournamentActorDefinition>(),
                startAreas: Array.Empty<TournamentStartAreaDefinition>(),
                entryItems: Array.Empty<TournamentEntryItemDefinition>());
            var actors = new List<Dungeon.MonsterSumInfo>();
            for (var index = 0; index < 4; index++)
            {
                actors.Add(new Dungeon.MonsterSumInfo
                {
                    Code = 1000 + index,
                    Level = 1,
                    Type = index == 3 ? (byte)3 : (byte)0,
                    IsBlocking = true,
                });
            }

            var runtime = new TournamentDungeonRuntime(
                definition,
                Array.Empty<TournamentRoundSnapshot>(),
                actors);
            if (!runtime.TryBindFirstActorSequence(1))
                throw new InvalidOperationException("fixture sequence bind failed");

            var run = new DungeonRun(1, 0);
            if (!run.Instance.Mechanisms.TryAttachTournament(runtime))
                throw new InvalidOperationException("fixture runtime attach failed");
            for (ushort sequence = 1; sequence <= 4; sequence++)
            {
                var source = DungeonEventEnvelope.Create(
                    run,
                    sourcePlayerId: 1,
                    cause: "tournament-selftest",
                    sourceActorId: sequence,
                    sourceActorCode: actors[sequence - 1].Code);
                runtime.TryApplyActorDeath(new DungeonActorDeathFact(
                    source,
                    sequence,
                    actors[sequence - 1].Code,
                    actors[sequence - 1].Type,
                    DungeonActorDeathKind.Defeated));
            }
            if (!runtime.IsChampion)
                throw new InvalidOperationException("fixture tournament did not finish");

            var state = new TournamentParticipantRewardState(
                new ClearRewardGenerator.CardReward[4],
                partyCount: 1,
                localPartySlot: 0,
                rewardExperience: 100,
                completedRounds: 4,
                completedAllRounds: true);
            Deliver(state, cardType: 0);
            Deliver(state, cardType: 1);
            run.Settlement.Tournament = state;
            return run;
        }

        private static void Deliver(
            TournamentParticipantRewardState state,
            byte cardType)
        {
            if (!state.TryReserveSelection(cardType, 0, out _)
                || !state.TryMarkDelivered(cardType, 0))
            {
                throw new InvalidOperationException(
                    "fixture tournament reward delivery failed");
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
