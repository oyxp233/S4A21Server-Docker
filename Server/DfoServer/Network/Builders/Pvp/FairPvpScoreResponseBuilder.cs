using System;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders.Pvp
{
    internal static class FairPvpScoreResponseBuilder
    {
        internal static byte[] BuildBasicBody(
            ushort targetUserId, byte viewMode, byte pvpGrade, byte pvpRatingGrade)
            => BuildBody(targetUserId, viewMode, pvpGrade, pvpRatingGrade, null);

        internal static byte[] BuildBody(ushort targetUserId, byte viewMode,
            byte pvpGrade, byte pvpRatingGrade, PvpSeasonScore score)
        {
            if (targetUserId == 0 || targetUserId == ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(targetUserId));
            if (viewMode < 1 || viewMode > 3)
                throw new ArgumentOutOfRangeException(nameof(viewMode));

            // A21 2743B80 consumes the success byte in the dispatcher, then
            // reads this basic record even when the detailed-record flag is 0.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(targetUserId);
            writer.WriteByte(viewMode);
            writer.WriteByte(score == null ? (byte)0 : (byte)1);
            writer.WriteByte(pvpGrade);
            writer.WriteByte(pvpRatingGrade);
            // 2743B80 -> FA6D60 / FA93D0: individual wins, relay wins,
            // recent ten results, current streak, double/all kills, peak streak.
            writer.WriteInt32(score?.Individual.Wins ?? 0);
            writer.WriteInt32(score?.Relay.Wins ?? 0);
            writer.WriteByte(score?.RecentWins ?? 0);
            writer.WriteByte(score?.RecentLosses ?? 0);
            writer.WriteByte(score?.RecentDraws ?? 0);
            writer.WriteInt32(score?.WinStreak ?? 0);
            writer.WriteInt32(0); // timed double kills have no authoritative owner
            writer.WriteInt32(0); // relay all-kill awards have no authoritative owner
            writer.WriteInt32(score?.PeakWinStreak ?? 0);
            if (score != null)
            {
                writer.WriteInt32(score.Individual.Losses);
                writer.WriteInt32(score.Individual.Draws);
                writer.WriteInt32(score.Relay.Losses);
                writer.WriteInt32(score.Relay.Draws);
                writer.WriteInt32(score.Team.Wins);
                writer.WriteInt32(score.Team.Losses);
                writer.WriteInt32(score.Team.Draws);
                writer.WriteByte(checked((byte)score.Jobs.Count));
                foreach (var job in score.Jobs)
                {
                    writer.WriteByte(job.Job);
                    writer.WriteByte(job.GrowType);
                    writer.WriteInt32(job.Score.Wins);
                    writer.WriteInt32(job.Score.Losses);
                    writer.WriteInt32(job.Score.Draws);
                }
            }
            return writer.ToArray();
        }
    }
}
