using DfoServer.Game.Dungeon;
using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal static class DungeonCommandParser
    {
        internal static bool TryParse(
            ushort wireType,
            byte[] body,
            out DungeonCommand command,
            out string error)
        {
            command = null;
            error = string.Empty;

            switch (wireType)
            {
                case (ushort)CmdPacketType.SUMMON_MONSTER:
                    return TryParseSummonMonster(
                        wireType,
                        body,
                        out command,
                        out error);

                case (ushort)CmdPacketType.TIMER_MODIFY_INFO:
                    command = new TimerModifyInfoDungeonCommand(
                        wireType,
                        body);
                    return true;

                case (ushort)CmdPacketType.SEA_CHASE_MINI_GAME_RESULT:
                    if (body == null || body.Length < 4)
                    {
                        error =
                            "SEA_CHASE_MINI_GAME_RESULT requires 4 bytes, " +
                            $"got {body?.Length ?? 0}";
                        return false;
                    }

                    command = new SeaChaseResultDungeonCommand(
                        wireType,
                        BitConverter.ToInt32(body, 0));
                    return true;

                case (ushort)CmdPacketType.EVENT_NPC_DROP_ITEM_:
                    command = new NpcItemDropDungeonCommand(wireType, body);
                    return true;

                case (ushort)CmdPacketType.BREAK_TRAP_RESULT:
                    command = new BreakTrapResultDungeonCommand(wireType, body);
                    return true;

                case (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE:
                    if (body != null && body.Length != 0)
                    {
                        error =
                            $"TOURNAMENT_REWARD_SELECT_STATE requires empty body, " +
                            $"got {body.Length}";
                        return false;
                    }
                    command = new TournamentRewardSelectStateDungeonCommand(
                        wireType);
                    return true;

                case (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT:
                    if (body == null || body.Length != 2)
                    {
                        error =
                            $"TOURNAMENT_REWARD_SELECT requires 2 bytes, " +
                            $"got {body?.Length ?? 0}";
                        return false;
                    }
                    if (body[0] >= 2 || body[1] >= 2)
                    {
                        error =
                            $"TOURNAMENT_REWARD_SELECT has invalid card " +
                            $"type={body[0]} index={body[1]}";
                        return false;
                    }
                    command = new TournamentRewardSelectDungeonCommand(
                        wireType,
                        body[0],
                        body[1]);
                    return true;

                case (ushort)CmdPacketType.BLOOD_ROUND_UI_PREPARE_FINISH_:
                    if (body != null && body.Length != 0)
                    {
                        error =
                            $"BLOOD_ROUND_UI_PREPARE_FINISH requires empty body, " +
                            $"got {body.Length}";
                        return false;
                    }
                    command = new BloodAltarPrepareFinishedDungeonCommand(
                        wireType);
                    return true;

                case (ushort)CmdPacketType.DIE_BLOOD_MONSTER:
                    return TryParseBloodAltarMonsterDeaths(
                        wireType,
                        body,
                        out command,
                        out error);

                case (ushort)CmdPacketType.SELECT_ULTIMATE_DIFFICULTY:
                    if (body == null || body.Length != 1)
                    {
                        error =
                            $"SELECT_ULTIMATE_DIFFICULTY requires 1 byte, " +
                            $"got {body?.Length ?? 0}";
                        return false;
                    }
                    if (body[0] != 1 && body[0] != 2)
                    {
                        error =
                            $"SELECT_ULTIMATE_DIFFICULTY has invalid " +
                            $"difficulty={body[0]}";
                        return false;
                    }
                    command = new BloodAltarSelectDifficultyDungeonCommand(
                        wireType,
                        body[0]);
                    return true;

                case 0x013C:
                case 0x0270:
                    command = new SeaChaseObservedDungeonCommand(
                        wireType,
                        body);
                    return true;

                default:
                    error = $"unsupported dungeon command 0x{wireType:X4}";
                    return false;
            }
        }

        private static bool TryParseSummonMonster(
            ushort wireType,
            byte[] body,
            out DungeonCommand command,
            out string error)
        {
            command = null;
            error = string.Empty;
            if (body == null || body.Length < 19)
            {
                error =
                    $"SUMMON_MONSTER requires 19 bytes, got {body?.Length ?? 0}";
                return false;
            }

            var parsed = new SummonMonsterDungeonCommand(
                wireType,
                BitConverter.ToUInt16(body, 0),
                BitConverter.ToInt32(body, 2),
                BitConverter.ToInt32(body, 6),
                BitConverter.ToInt32(body, 10),
                BitConverter.ToUInt16(body, 14),
                BitConverter.ToUInt16(body, 16),
                body[18]);
            if (parsed.MonsterCode <= 0
                || parsed.StateId <= 0
                || parsed.MapId <= 0
                || parsed.MatchCount == 0)
            {
                error =
                    "SUMMON_MONSTER contains an invalid monster, state, " +
                    "map or match count";
                return false;
            }

            command = parsed;
            return true;
        }

        private static bool TryParseBloodAltarMonsterDeaths(
            ushort wireType,
            byte[] body,
            out DungeonCommand command,
            out string error)
        {
            command = null;
            error = string.Empty;
            if (body == null || body.Length < 1)
            {
                error = "DIE_BLOOD_MONSTER requires a count byte";
                return false;
            }

            var count = body[0];
            var expectedLength = 1 + count * sizeof(ushort);
            if (count == 0 || body.Length != expectedLength)
            {
                error =
                    $"DIE_BLOOD_MONSTER requires 1 + count * 2 bytes " +
                    $"with count > 0, count={count} got={body.Length}";
                return false;
            }

            var sequences = new ushort[count];
            for (var index = 0; index < count; index++)
            {
                sequences[index] = BitConverter.ToUInt16(
                    body,
                    1 + index * sizeof(ushort));
                if (sequences[index] == 0)
                {
                    error =
                        $"DIE_BLOOD_MONSTER contains zero sequence at {index}";
                    return false;
                }
            }

            command = new BloodAltarMonsterDeathsDungeonCommand(
                wireType,
                sequences);
            return true;
        }
    }
}
