using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Settlement-time behavior owned by ordinary special-dungeon mechanisms.
    // Generic settlement still owns phase transitions, rewards and card flow.
    internal static class SpecialDungeonSettlementCoordinator
    {
        internal static async Task OnDungeonClearedAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || special.Kind != SpecialDungeonKind.SeizeMoney)
            {
                return;
            }

            var dungeonLevel = 0;
            try
            {
                dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"reason=dungeon_level error={ex.Message}");
                return;
            }

            var bossSequence = special.SeizeMoneyBossSeq;
            var bossCode = ResolveBossCode(run, bossSequence);
            if (bossCode <= 0
                || !IndependentDropSystem.TryResolveSingleFixedDropTemplate(
                    bossCode,
                    run.Difficulty,
                    dungeonLevel,
                    run.EntryPartyMemberCount,
                    out var rewardItemId,
                    out var maxDropCount))
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"boss={bossCode} bossSeq={bossSequence} " +
                    $"reason=fixed_drop_not_unique");
                return;
            }

            if (bossSequence == 0)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"reason=missing_boss_sequence");
                return;
            }
            if (!special.TryReserveAuthoritativeSeizeMoneyClearReward(
                    maxDropCount,
                    out var rewardPlan,
                    out var failureReason))
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"bossSeq={bossSequence} reason={failureReason} " +
                    $"gauge={special.SeizeMoneyGauge}");
                return;
            }
            if (rewardPlan.Count <= 0)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"bossSeq={bossSequence} reason=no_remaining_reward " +
                    $"hitCount={rewardPlan.HitCount} " +
                    $"remainingUnits={rewardPlan.RemainingUnits} " +
                    $"gauge={rewardPlan.Gauge}");
                return;
            }

            var drops = new List<DropInfo>();
            lock (run.SyncRoot)
            {
                for (var i = 0; i < rewardPlan.Count; i++)
                {
                    run.SceneSlotCounter++;
                    var drop = new DropInfo
                    {
                        SceneSlot = run.SceneSlotCounter,
                        TemplateId = (uint)rewardItemId,
                        StackCount = 1,
                    };
                    drops.Add(drop);
                    run.Drops[drop.SceneSlot] = drop;
                }
            }

            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.DIE_MONSTER,
                DungeonNotificationBuilder.BuildMonsterDie(
                    bossSequence,
                    drops,
                    session.Player.UserId)));
            FileLogger.Log(
                $"[SpecialDungeonModule] SEIZE_MONEY drops sent: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"boss={bossCode} bossSeq={bossSequence} " +
                $"item={rewardItemId} count={rewardPlan.Count}/{maxDropCount} " +
                $"hitCount={rewardPlan.HitCount} " +
                $"remainingUnits={rewardPlan.RemainingUnits} " +
                $"gauge={rewardPlan.Gauge}/" +
                $"{special.Definition.SeizeMoney.GaugeMax}");
        }

        private static int ResolveBossCode(DungeonRun run, ushort bossSequence)
        {
            if (run.BossCode > 0)
                return run.BossCode;
            if (bossSequence == 0)
                return 0;

            lock (run.SyncRoot)
            {
                var localIndex = (int)bossSequence - run.RoomStartSequence;
                if (run.RoomMonsters == null
                    || localIndex < 0
                    || localIndex >= run.RoomMonsters.Count)
                {
                    return 0;
                }

                return run.RoomMonsters[localIndex].Code;
            }
        }

    }
}
