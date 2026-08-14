using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TimeSpiralData = DfoServer.GameWorld.TimeSpiralDungeonData;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class TimeSpiralDungeonCoordinator
    {
        internal sealed class TeleportMoveContext
        {
            internal int TrapMapId;
            internal int TargetX;
            internal int TargetY;
            internal int TargetWeight;
            internal int TargetFlag;
            internal TimeSpiralData.TrapBuff Buff;
            internal int BuffRoll;
            internal int BuffTotalWeight;
        }

        internal static bool IsDungeon(int dungeonId)
            => TimeSpiralData.IsDungeon(dungeonId);

        internal static async Task HandleBreakTrapResultAsync(
            EnhancedClientSession session,
            BreakTrapResultDungeonCommand command,
            DungeonEventEnvelope sourceEvent)
        {
            var run = session?.Player?.CurrentRun;
            var roomIdentity = run?.CaptureParticipantRoomIdentity()
                ?? default(DungeonParticipantRoomIdentity);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                command?.WireType ?? (ushort)CmdPacketType.BREAK_TRAP_RESULT,
                CommonPacketBodyBuilder.BuildSuccessAck()));

            if (run == null
                || command == null
                || sourceEvent == null
                || !sourceEvent.RunIdentity.Equals(roomIdentity.Run)
                || (sourceEvent.RoomInstanceId.HasValue
                    && sourceEvent.RoomInstanceId.Value != roomIdentity.RoomInstanceId)
                || !session.Player.IsCurrentDungeonParticipantRoom(roomIdentity)
                || !IsDungeon(run.DungeonId))
                return;

            var mapId = ResolveCurrentMapId(run);
            FileLogger.Log(
                $"[TimeSpiral] BREAK_TRAP_RESULT: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"room=({run.RoomKey.X},{run.RoomKey.Y}) map={mapId} " +
                $"body={(command.Payload.Length == 0 ? string.Empty : BitConverter.ToString(command.Payload))}");

            if (TryMarkTeleportPending(
                    session,
                    run,
                    mapId,
                    "BREAK_TRAP_RESULT"))
            {
                await SendConditionPassGateAsync(
                    session,
                    run,
                    roomIdentity,
                    mapId,
                    0,
                    "cmd1 BREAK_TRAP_RESULT",
                    string.Empty);
            }
        }

        internal static TeleportMoveContext ApplyTeleportOverride(
            EnhancedClientSession session,
            int requestedX,
            int requestedY,
            ref DungeonRoomPoint moveTarget)
            => ApplyTeleportOverride(
                session,
                session?.Player?.CurrentRun,
                requestedX,
                requestedY,
                ref moveTarget);

        internal static TeleportMoveContext ApplyTeleportOverride(
            EnhancedClientSession session,
            DungeonRun run,
            int requestedX,
            int requestedY,
            ref DungeonRoomPoint moveTarget)
        {
            if (session?.Player == null
                || run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                || !run.TimeSpiralTeleportPending
                || !IsDungeon(run.DungeonId)
                || run.TimeSpiralTargetX < 0
                || run.TimeSpiralTargetY < 0)
            {
                return null;
            }

            var normalTarget = moveTarget;
            var context = new TeleportMoveContext
            {
                TrapMapId = run.TimeSpiralTrapMapId,
                TargetX = run.TimeSpiralTargetX,
                TargetY = run.TimeSpiralTargetY,
                TargetWeight = run.TimeSpiralTargetWeight,
                TargetFlag = run.TimeSpiralTargetFlag,
            };

            run.TimeSpiralTeleportPending = false;
            ClearHiddenBoss(run);
            moveTarget = new DungeonRoomPoint(
                context.TargetX,
                context.TargetY);

            if (TimeSpiralData.TryPickTrapBuff(
                    null,
                    out var buff,
                    out var roll,
                    out var totalWeight))
            {
                context.Buff = buff;
                context.BuffRoll = roll;
                context.BuffTotalWeight = totalWeight;
            }

            FileLogger.Log(
                $"[TimeSpiral] teleport consumed: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"trapMap={context.TrapMapId} current=({run.RoomKey.X},{run.RoomKey.Y}) " +
                $"requested=({requestedX},{requestedY}) " +
                $"normalTarget=({normalTarget.X},{normalTarget.Y}) " +
                $"target=({context.TargetX},{context.TargetY}) " +
                $"weight={context.TargetWeight} flag={context.TargetFlag}");
            return context;
        }

        internal static void CopyTeleportStateForPartyMove(
            DungeonRun leaderRun,
            DungeonRun memberRun)
        {
            if (leaderRun == null
                || memberRun == null
                || !IsDungeon(leaderRun.DungeonId))
            {
                return;
            }

            memberRun.TimeSpiralTeleportPending = false;
            memberRun.TimeSpiralTrapMapId = leaderRun.TimeSpiralTrapMapId;
            memberRun.TimeSpiralTargetActive = leaderRun.TimeSpiralTargetActive;
            memberRun.TimeSpiralTargetX = leaderRun.TimeSpiralTargetX;
            memberRun.TimeSpiralTargetY = leaderRun.TimeSpiralTargetY;
            memberRun.TimeSpiralTargetFlag = leaderRun.TimeSpiralTargetFlag;
            memberRun.TimeSpiralTargetWeight = leaderRun.TimeSpiralTargetWeight;
            ClearHiddenBoss(memberRun);
        }

        internal static void RegisterHiddenBossAfterStartMap(
            EnhancedClientSession session,
            RoomState roomState)
            => RegisterHiddenBossAfterStartMap(
                session,
                session?.Player?.CurrentRun,
                roomState);

        internal static void RegisterHiddenBossAfterStartMap(
            EnhancedClientSession session,
            DungeonRun run,
            RoomState roomState)
        {
            if (session?.Player == null
                || run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                || roomState == null
                || !IsHiddenBossRegistrationRoom(run, roomState))
            {
                return;
            }

            if (!TimeSpiralData.TryFindHiddenBossCandidate(
                    roomState.Maze,
                    roomState.FirstSeqId,
                    out var candidate))
            {
                FileLogger.Log(
                    $"[TimeSpiral] hidden boss not found: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"room=({roomState.Maze.X},{roomState.Maze.Y}) " +
                    $"map={roomState.Maze.Index} flag={run.TimeSpiralTargetFlag}");
                return;
            }

            roomState.TimeSpiralHiddenBossActive = true;
            roomState.TimeSpiralHiddenBossSeqId = candidate.SequenceId;
            roomState.TimeSpiralHiddenBossCode = candidate.Code;
            roomState.TimeSpiralHiddenBossSource = candidate.MonsterPath;

            run.TimeSpiralHiddenBossActive = true;
            run.TimeSpiralHiddenBossSeqId = candidate.SequenceId;
            run.TimeSpiralHiddenBossCode = candidate.Code;
            run.TimeSpiralHiddenBossMapId = roomState.Maze.Index;
            run.TimeSpiralHiddenBossX = roomState.Maze.X;
            run.TimeSpiralHiddenBossY = roomState.Maze.Y;
            run.TimeSpiralHiddenBossSource = candidate.MonsterPath;

            var source = IsFinalBossRoom(run, roomState)
                ? "dgn_boss_map"
                : "etc_flag0_target";
            FileLogger.Log(
                $"[TimeSpiral] hidden boss registered: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"room=({roomState.Maze.X},{roomState.Maze.Y}) " +
                $"map={roomState.Maze.Index} seq={candidate.SequenceId} " +
                $"local={candidate.LocalIndex} code={candidate.Code} " +
                $"type={candidate.Type} path={candidate.MonsterPath} source={source}");
        }

        internal static void RestoreHiddenBoss(
            DungeonRun run,
            RoomState roomState)
        {
            if (run == null
                || roomState == null
                || !roomState.TimeSpiralHiddenBossActive)
            {
                return;
            }

            run.TimeSpiralHiddenBossActive = true;
            run.TimeSpiralHiddenBossSeqId =
                roomState.TimeSpiralHiddenBossSeqId;
            run.TimeSpiralHiddenBossCode =
                roomState.TimeSpiralHiddenBossCode;
            run.TimeSpiralHiddenBossMapId = roomState.Maze.Index;
            run.TimeSpiralHiddenBossX = roomState.Maze.X;
            run.TimeSpiralHiddenBossY = roomState.Maze.Y;
            run.TimeSpiralHiddenBossSource =
                roomState.TimeSpiralHiddenBossSource;
        }

        internal static bool IsTrackedHiddenBossKill(
            DungeonRun run,
            RoomState roomState,
            ushort killedSequenceId,
            int killedMonsterCode)
        {
            if (run == null
                || roomState == null
                || killedSequenceId == 0
                || killedMonsterCode <= 0
                || !run.TimeSpiralHiddenBossActive
                || !IsDungeon(run.DungeonId))
            {
                return false;
            }

            var expectedSequenceId = roomState.TimeSpiralHiddenBossActive
                ? roomState.TimeSpiralHiddenBossSeqId
                : run.TimeSpiralHiddenBossSeqId;
            var expectedCode = roomState.TimeSpiralHiddenBossActive
                ? roomState.TimeSpiralHiddenBossCode
                : run.TimeSpiralHiddenBossCode;

            return roomState.Maze.X == run.TimeSpiralHiddenBossX
                && roomState.Maze.Y == run.TimeSpiralHiddenBossY
                && roomState.Maze.Index == run.TimeSpiralHiddenBossMapId
                && expectedSequenceId == killedSequenceId
                && expectedCode == killedMonsterCode;
        }

        internal static bool IsHiddenBossRegistrationRoom(
            DungeonRun run,
            RoomState roomState)
        {
            if (run == null
                || roomState == null
                || !IsDungeon(run.DungeonId))
            {
                return false;
            }

            return IsFinalBossRoom(run, roomState)
                || (run.TimeSpiralTargetActive
                    && run.TimeSpiralTargetFlag == 0
                    && run.TimeSpiralTargetX == roomState.Maze.X
                    && run.TimeSpiralTargetY == roomState.Maze.Y);
        }

        internal static void LogDeferredBuff(
            EnhancedClientSession session,
            TeleportMoveContext context,
            string source)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || context == null || !IsDungeon(run.DungeonId))
                return;

            if (context.Buff == null)
            {
                FileLogger.Log(
                    $"[TimeSpiral] trap buff skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"source={source} result=no_valid_buff");
                return;
            }

            var buff = context.Buff;
            FileLogger.Log(
                $"[TimeSpiral] trap buff resolved: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"source={source} index={buff.Index} " +
                $"roll={context.BuffRoll}/{context.BuffTotalWeight} " +
                $"weight={buff.Weight} pa={buff.PhysicalAttack} " +
                $"ma={buff.MagicalAttack} move={buff.MoveSpeed} " +
                $"attack={buff.AttackSpeed} cast={buff.CastSpeed} " +
                $"time={buff.BuffTimeMs} " +
                $"packet=deferred reason=no_confirmed_buff_id");
        }

        private static bool TryMarkTeleportPending(
            EnhancedClientSession session,
            DungeonRun run,
            int trapMapId,
            string source)
        {
            if (run == null
                || !IsDungeon(run.DungeonId))
            {
                FileLogger.Log(
                    $"[TimeSpiral] teleport target missing: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"dungeon={run?.DungeonId ?? 0} trapMap={trapMapId} " +
                    $"source={source}");
                return false;
            }

            lock (run.SyncRoot)
            {
                if (run.TimeSpiralTeleportPending)
                {
                    FileLogger.Log(
                        $"[TimeSpiral] teleport request ignored: " +
                        $"cid={session.Player.CharacterId} " +
                        $"dungeon={run.DungeonId} trapMap={trapMapId} " +
                        $"source={source} reason=already_pending");
                    return false;
                }

                if (!TimeSpiralData.TryPickTeleportTarget(
                        trapMapId,
                        out var target))
                {
                    FileLogger.Log(
                        $"[TimeSpiral] teleport target missing: " +
                        $"cid={session.Player.CharacterId} " +
                        $"dungeon={run.DungeonId} trapMap={trapMapId} " +
                        $"source={source}");
                    return false;
                }

                run.TimeSpiralTeleportPending = true;
                run.TimeSpiralTrapMapId = trapMapId;
                run.TimeSpiralTargetActive = true;
                run.TimeSpiralTargetX = target.X;
                run.TimeSpiralTargetY = target.Y;
                run.TimeSpiralTargetFlag = target.Flag;
                run.TimeSpiralTargetWeight = target.Weight;
                FileLogger.Log(
                    $"[TimeSpiral] teleport pending: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"trapMap={trapMapId} target=({target.X},{target.Y}) " +
                    $"weight={target.Weight} flag={target.Flag} source={source}");
                return true;
            }
        }

        private static async Task SendConditionPassGateAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonParticipantRoomIdentity roomIdentity,
            int mapId,
            int objectCode,
            string source,
            string objectPath)
        {
            if (run == null
                || !session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
            {
                return;
            }

            await DungeonMechanismNotificationSender
                .SendCompleteConditionPassGateAsync(
                    session,
                    "time-spiral-trap",
                    source);
            if (!session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
                return;
            FileLogger.Log(
                $"[TimeSpiral] trap condition advanced: " +
                $"cid={session.Player.CharacterId} " +
                $"dungeon={run.DungeonId} " +
                $"map={mapId} object={objectCode} source={source} " +
                $"path={objectPath}");
        }

        private static int ResolveCurrentMapId(DungeonRun run)
        {
            if (run?.RoomStates != null
                && run.RoomStates.TryGetValue(run.RoomKey, out var roomState))
            {
                return roomState?.Maze.Index ?? 0;
            }

            return 0;
        }

        private static bool IsFinalBossRoom(
            DungeonRun run,
            RoomState roomState)
        {
            return run?.BossMapPos != null
                && run.BossMapPos.Length >= 2
                && roomState != null
                && roomState.Maze.X == run.BossMapPos[0]
                && roomState.Maze.Y == run.BossMapPos[1];
        }

        private static void ClearHiddenBoss(DungeonRun run)
        {
            run.TimeSpiralHiddenBossActive = false;
            run.TimeSpiralHiddenBossSeqId = 0;
            run.TimeSpiralHiddenBossCode = 0;
            run.TimeSpiralHiddenBossMapId = 0;
            run.TimeSpiralHiddenBossX = -1;
            run.TimeSpiralHiddenBossY = -1;
            run.TimeSpiralHiddenBossSource = null;
        }
    }
}
