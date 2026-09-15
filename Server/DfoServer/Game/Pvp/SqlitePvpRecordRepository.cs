using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Pvp
{
    internal sealed class SqlitePvpRecordRepository
    {
        private readonly IGameDatabase _database;

        internal SqlitePvpRecordRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal PvpRecord Load(int characterId)
        {
            using var connection = _database.OpenConnection();
            return Load(connection, null, characterId);
        }

        private static PvpRecord Load(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT c.pvp_grade, c.pvp_rating_grade,
       COALESCE(p.experience_in_grade, 0), COALESCE(p.win_count, 0), COALESCE(p.loss_count, 0),
       COALESCE(p.rank_point, 0), COALESCE(p.peak_rank_point, 0), COALESCE(p.rank_warmup_games, 0),
       COALESCE(p.total_match_point, 0), COALESCE(p.total_match_warmup_games, 0)
FROM characters c LEFT JOIN character_pvp_records p ON p.character_id=c.character_id
WHERE c.character_id=@cid AND c.delete_flag=0;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var grade = checked((byte)reader.GetInt32(0));
            var rating = checked((byte)reader.GetInt32(1));
            var bounds = PvpExperienceRules.Current.GetBounds(grade);
            var progress = reader.GetInt32(2);
            if (progress < 0 || progress > bounds.Ceiling - bounds.Floor)
                throw new InvalidDataException($"PvP experience is outside grade {grade}: character {characterId}.");

            return new PvpRecord
            {
                Grade = grade,
                RatingGrade = rating,
                Experience = checked(bounds.Floor + progress),
                ExperienceFloor = bounds.Floor,
                ExperienceCeiling = bounds.Ceiling,
                Wins = reader.GetInt32(3),
                Losses = reader.GetInt32(4),
                RankPoint = reader.GetInt32(5),
                PeakRankPoint = reader.GetInt32(6),
                RankWarmupGames = reader.GetInt32(7),
                TotalMatchPoint = reader.GetInt32(8),
                TotalMatchWarmupGames = reader.GetInt32(9)
            };
        }

        // One transaction owns the complete match, every participant's result,
        // the grade in characters, and its within-grade progress. Neither rank
        // ACKs nor timers supply experience, RP/GP, or client-reported scores.
        internal PvpMatchSettlement Settle(FreeDuelRoom room, bool recordProgress, DateTime utcNow)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));
            if (room.MatchGeneration <= 0 || room.MatchParticipants.Count < 2
                || room.SettlementPhase != FreeDuelRoom.AwaitingRankSettlementPhase
                    && room.SettlementPhase != FreeDuelRoom.AwaitingEndSettlementPhase)
                throw new InvalidOperationException("PvP match has no terminal combat result.");

            var participants = room.MatchParticipants;
            if (participants.Select(p => p.CharacterId).Distinct().Count() != participants.Count
                || participants.Select(p => p.Seat).Distinct().Count() != participants.Count
                || participants.Any(p => p.CharacterId <= 0 || p.Seat >= FreeDuelRoom.SeatCount
                    || p.UserId == 0 || p.UserId == ushort.MaxValue || p.Team > 2)
                || room.WinnerSeat != byte.MaxValue && participants.All(p => p.Seat != room.WinnerSeat))
                throw new InvalidDataException("PvP match participant identity is inconsistent.");

            var characterIds = participants.Select(p => p.CharacterId)
                .Concat(Enumerable.Range(0, FreeDuelRoom.SeatCount)
                    .Where(room.IsOccupiedSeat).Select(room.GetSeatCharacterId)).Distinct().ToArray();
            var rewardsEnabled = recordProgress && room.BattleMode >= 1 && room.BattleMode <= 3;
            if (!rewardsEnabled)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(deferred: true);
                return new PvpMatchSettlement { Players = LoadPlayers(connection, transaction, characterIds) };
            }

            var rules = PvpExperienceRules.Current;
            var day = DailyResetService.TodayId(utcNow);
            return _database.Write((connection, transaction) =>
            {
                var players = LoadPlayers(connection, transaction, characterIds);
                using (var command = MatchCommand(connection, transaction, room))
                {
                    command.CommandText = @"
SELECT listener_port,battle_mode,winner_seat,season FROM pvp_matches
WHERE room_generation=@generation AND match_generation=@match;";
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        if (reader.GetInt32(0) != room.ListenerPort || reader.GetByte(1) != room.BattleMode
                            || reader.GetByte(2) != room.WinnerSeat || reader.GetByte(3) != PvpSeasonScore.CurrentSeason)
                            throw new InvalidDataException("Conflicting PvP result for the same match generation.");
                        reader.Close();
                        ReadCommittedResult(connection, transaction, room, players);
                        return new PvpMatchSettlement { Players = players };
                    }
                }

                using (var command = MatchCommand(connection, transaction, room))
                {
                    command.CommandText = @"
INSERT INTO pvp_matches(room_generation,match_generation,listener_port,battle_mode,winner_seat,
                       season,game_day,completed_at_utc)
VALUES(@generation,@match,@port,@mode,@winner,@season,@day,@now);";
                    command.Parameters.AddWithValue("@port", room.ListenerPort);
                    command.Parameters.AddWithValue("@mode", room.BattleMode);
                    command.Parameters.AddWithValue("@winner", room.WinnerSeat);
                    command.Parameters.AddWithValue("@season", PvpSeasonScore.CurrentSeason);
                    command.Parameters.AddWithValue("@day", day);
                    command.Parameters.AddWithValue("@now", utcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }

                var winner = participants.FirstOrDefault(p => p.Seat == room.WinnerSeat);
                foreach (var participant in participants)
                {
                    var outcome = winner == null ? PvpMatchOutcome.Draw
                        : (room.BattleMode == 1 ? participant.Seat == winner.Seat : participant.Team == winner.Team)
                            ? PvpMatchOutcome.Win : PvpMatchOutcome.Loss;
                    var before = players[participant.CharacterId].Record;
                    var change = rules.GetExperienceChange(before.Grade, outcome);
                    // Current A21 text 72726 specifies 50 winning / 10 losing
                    // results per game day for experience eligibility.
                    var limit = outcome == PvpMatchOutcome.Win ? 50 : 10;
                    if (CountDailyResults(connection, transaction, participant.CharacterId, day, outcome) >= limit)
                        change = 0;
                    var experience = change == 0 ? before.Experience
                        : checked((int)Math.Clamp((long)before.Experience + change, 0, rules.MaximumExperience));
                    // A21 1368FA0 uses text 191 for grade zero: one win promotes
                    // to bronze one star. The ordinary PVF award alone is smaller
                    // than that threshold; keep the cumulative projection valid.
                    if (before.Grade == 0 && outcome == PvpMatchOutcome.Win && change > 0)
                        experience = Math.Max(experience, rules.GetBounds(1).Floor);
                    var after = before.WithProgress(experience,
                        checked(before.Wins + (outcome == PvpMatchOutcome.Win ? 1 : 0)),
                        checked(before.Losses + (outcome == PvpMatchOutcome.Loss ? 1 : 0)), rules);
                    var previousStreak = ReadLatestStreak(connection, transaction, participant.CharacterId);
                    var streak = outcome == PvpMatchOutcome.Win ? checked(previousStreak.Current + 1) : 0;
                    var peakStreak = Math.Max(previousStreak.Peak, streak);
                    var opponent = participants.Count == 2
                        ? participants.First(p => p.CharacterId != participant.CharacterId) : null;
                    SaveProgress(connection, transaction, participant.CharacterId, after);
                    using (var command = MatchCommand(connection, transaction, room))
                    {
                        command.CommandText = @"
INSERT INTO pvp_match_results(room_generation,match_generation,character_id,seat,user_id,team,outcome,
    kills,deaths,previous_grade,grade,experience_change,experience,win_streak,peak_win_streak,
    opponent_job,opponent_grow_type)
VALUES(@generation,@match,@cid,@seat,@uid,@team,@outcome,@kills,@deaths,@beforeGrade,@grade,@change,@experience,
    @streak,@peak,(SELECT job FROM characters WHERE character_id=@opponent),
    (SELECT grow_type & 15 FROM characters WHERE character_id=@opponent));";
                        command.Parameters.AddWithValue("@cid", participant.CharacterId);
                        command.Parameters.AddWithValue("@seat", participant.Seat);
                        command.Parameters.AddWithValue("@uid", participant.UserId);
                        command.Parameters.AddWithValue("@team", participant.Team);
                        command.Parameters.AddWithValue("@outcome", (byte)outcome);
                        command.Parameters.AddWithValue("@kills", room.GetKillCount(participant.Seat));
                        command.Parameters.AddWithValue("@deaths", room.GetDeathCount(participant.Seat));
                        command.Parameters.AddWithValue("@beforeGrade", before.Grade);
                        command.Parameters.AddWithValue("@grade", after.Grade);
                        command.Parameters.AddWithValue("@change", after.Experience - before.Experience);
                        command.Parameters.AddWithValue("@experience", after.Experience);
                        command.Parameters.AddWithValue("@streak", streak);
                        command.Parameters.AddWithValue("@peak", peakStreak);
                        command.Parameters.AddWithValue("@opponent", (object)opponent?.CharacterId ?? DBNull.Value);
                        command.ExecuteNonQuery();
                    }
                    players[participant.CharacterId] = new PvpMatchProgress
                    {
                        Record = after,
                        PreviousGrade = before.Grade,
                        ExperienceChange = after.Experience - before.Experience
                    };
                }
                return new PvpMatchSettlement { Players = players, NewlyCommitted = true };
            });
        }

        private static Dictionary<int, PvpMatchProgress> LoadPlayers(
            SqliteConnection connection, SqliteTransaction transaction, IEnumerable<int> characterIds)
        {
            var players = new Dictionary<int, PvpMatchProgress>();
            foreach (var characterId in characterIds)
            {
                var record = Load(connection, transaction, characterId)
                    ?? throw new InvalidOperationException($"PvP result character {characterId} is unavailable.");
                players.Add(characterId, new PvpMatchProgress { Record = record, PreviousGrade = record.Grade });
            }
            return players;
        }

        private static SqliteCommand MatchCommand(SqliteConnection connection, SqliteTransaction transaction, FreeDuelRoom room)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@generation", room.GenerationId.ToString("N"));
            command.Parameters.AddWithValue("@match", room.MatchGeneration);
            return command;
        }

        private static void ReadCommittedResult(SqliteConnection connection, SqliteTransaction transaction,
            FreeDuelRoom room, IDictionary<int, PvpMatchProgress> players)
        {
            using var command = MatchCommand(connection, transaction, room);
            command.CommandText = @"
SELECT character_id,seat,user_id,team,kills,deaths,previous_grade,experience_change
FROM pvp_match_results WHERE room_generation=@generation AND match_generation=@match;";
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                var characterId = reader.GetInt32(0);
                var participant = room.MatchParticipants.FirstOrDefault(p => p.CharacterId == characterId);
                if (participant == null || participant.Seat != reader.GetByte(1)
                    || participant.UserId != reader.GetInt32(2) || participant.Team != reader.GetByte(3)
                    || room.GetKillCount(participant.Seat) != reader.GetInt32(4)
                    || room.GetDeathCount(participant.Seat) != reader.GetInt32(5))
                    throw new InvalidDataException("Conflicting PvP participant result for the same match generation.");
                players[characterId] = new PvpMatchProgress
                {
                    Record = players[characterId].Record,
                    PreviousGrade = reader.GetByte(6),
                    ExperienceChange = reader.GetInt32(7)
                };
                count++;
            }
            if (count != room.MatchParticipants.Count)
                throw new InvalidDataException("Incomplete durable PvP match result.");
        }

        private static void SaveProgress(SqliteConnection connection, SqliteTransaction transaction, int characterId, PvpRecord record)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO character_pvp_records(character_id,experience_in_grade,win_count,loss_count)
VALUES(@cid,@progress,@wins,@losses)
ON CONFLICT(character_id) DO UPDATE SET experience_in_grade=excluded.experience_in_grade,
    win_count=excluded.win_count,loss_count=excluded.loss_count;
UPDATE characters SET pvp_grade=@grade WHERE character_id=@cid AND delete_flag=0;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@progress", record.Experience - record.ExperienceFloor);
            command.Parameters.AddWithValue("@wins", record.Wins);
            command.Parameters.AddWithValue("@losses", record.Losses);
            command.Parameters.AddWithValue("@grade", record.Grade);
            if (command.ExecuteNonQuery() != 2)
                throw new InvalidOperationException("PvP participant is no longer an active character.");
        }

        private static int CountDailyResults(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int day, PvpMatchOutcome outcome)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT COUNT(*) FROM pvp_match_results r JOIN pvp_matches m
ON m.room_generation=r.room_generation AND m.match_generation=r.match_generation
WHERE r.character_id=@cid AND m.game_day=@day AND r.outcome=@outcome;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@day", day);
            command.Parameters.AddWithValue("@outcome", (byte)outcome);
            return checked((int)(long)command.ExecuteScalar());
        }

        private static (int Current, int Peak) ReadLatestStreak(SqliteConnection connection,
            SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT r.win_streak,r.peak_win_streak FROM pvp_match_results r JOIN pvp_matches m
ON m.room_generation=r.room_generation AND m.match_generation=r.match_generation
WHERE r.character_id=@cid AND m.season=@season ORDER BY r.result_id DESC LIMIT 1;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@season", PvpSeasonScore.CurrentSeason);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
        }

        internal PvpSeasonScore LoadSeasonScore(int characterId, byte season)
        {
            // First/second-season imports have no owner. A current match must
            // never appear as fabricated history when the user changes tabs.
            if (season != PvpSeasonScore.CurrentSeason)
                return new PvpSeasonScore();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            var modes = new Dictionary<byte, PvpModeScore>();
            using (var command = ScoreCommand(connection, transaction, characterId, season))
            {
                command.CommandText = @"
SELECT m.battle_mode,SUM(r.outcome=1),SUM(r.outcome=0),SUM(r.outcome=2)
FROM pvp_match_results r JOIN pvp_matches m
ON m.room_generation=r.room_generation AND m.match_generation=r.match_generation
WHERE r.character_id=@cid AND m.season=@season GROUP BY m.battle_mode;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    modes.Add(reader.GetByte(0), new PvpModeScore
                    { Wins = reader.GetInt32(1), Losses = reader.GetInt32(2), Draws = reader.GetInt32(3) });
            }
            byte wins = 0, losses = 0, draws = 0;
            using (var command = ScoreCommand(connection, transaction, characterId, season))
            {
                command.CommandText = @"
SELECT r.outcome FROM pvp_match_results r JOIN pvp_matches m
ON m.room_generation=r.room_generation AND m.match_generation=r.match_generation
WHERE r.character_id=@cid AND m.season=@season ORDER BY r.result_id DESC LIMIT 10;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    switch ((PvpMatchOutcome)reader.GetByte(0))
                    {
                        case PvpMatchOutcome.Win: wins++; break;
                        case PvpMatchOutcome.Loss: losses++; break;
                        case PvpMatchOutcome.Draw: draws++; break;
                    }
            }
            var jobs = new List<PvpJobScore>();
            using (var command = ScoreCommand(connection, transaction, characterId, season))
            {
                command.CommandText = @"
SELECT r.opponent_job,r.opponent_grow_type,SUM(r.outcome=1),SUM(r.outcome=0),SUM(r.outcome=2)
FROM pvp_match_results r JOIN pvp_matches m
ON m.room_generation=r.room_generation AND m.match_generation=r.match_generation
WHERE r.character_id=@cid AND m.season=@season AND r.opponent_job IS NOT NULL
GROUP BY r.opponent_job,r.opponent_grow_type ORDER BY r.opponent_job,r.opponent_grow_type;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    jobs.Add(new PvpJobScore
                    {
                        Job = reader.GetByte(0), GrowType = reader.GetByte(1),
                        Score = new PvpModeScore { Wins = reader.GetInt32(2), Losses = reader.GetInt32(3), Draws = reader.GetInt32(4) }
                    });
            }
            var streak = ReadLatestStreak(connection, transaction, characterId);
            return new PvpSeasonScore
            {
                Individual = modes.TryGetValue(1, out var individual) ? individual : new PvpModeScore(),
                Team = modes.TryGetValue(2, out var team) ? team : new PvpModeScore(),
                Relay = modes.TryGetValue(3, out var relay) ? relay : new PvpModeScore(),
                RecentWins = wins, RecentLosses = losses, RecentDraws = draws,
                WinStreak = streak.Current, PeakWinStreak = streak.Peak, Jobs = jobs
            };
        }

        private static SqliteCommand ScoreCommand(SqliteConnection connection, SqliteTransaction transaction, int characterId, byte season)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@season", season);
            return command;
        }
    }
}
