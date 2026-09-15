using System;
using System.Collections.Generic;

namespace DfoServer.Game.Pvp
{
    internal enum PvpMatchOutcome : byte
    {
        Loss = 0,
        Win = 1,
        Draw = 2
    }

    // Captured once at START_PVP. A departing member must remain attributable
    // to this match even after its waiting-room seat and session are removed.
    internal sealed class PvpMatchParticipant
    {
        internal byte Seat { get; init; }
        internal int CharacterId { get; init; }
        internal ushort UserId { get; init; }
        internal byte Team { get; init; }
    }

    internal sealed class PvpMatchProgress
    {
        internal PvpRecord Record { get; init; }
        internal byte PreviousGrade { get; init; }
        internal int ExperienceChange { get; init; }
    }

    internal sealed class PvpMatchSettlement
    {
        internal IReadOnlyDictionary<int, PvpMatchProgress> Players { get; init; }
        internal bool NewlyCommitted { get; init; }
    }

    internal sealed class PvpModeScore
    {
        internal int Wins { get; init; }
        internal int Losses { get; init; }
        internal int Draws { get; init; }
    }

    internal sealed class PvpJobScore
    {
        internal byte Job { get; init; }
        internal byte GrowType { get; init; }
        internal PvpModeScore Score { get; init; }
    }

    internal sealed class PvpSeasonScore
    {
        internal const byte CurrentSeason = 3;
        internal PvpModeScore Individual { get; init; } = new PvpModeScore();
        internal PvpModeScore Relay { get; init; } = new PvpModeScore();
        internal PvpModeScore Team { get; init; } = new PvpModeScore();
        internal byte RecentWins { get; init; }
        internal byte RecentLosses { get; init; }
        internal byte RecentDraws { get; init; }
        internal int WinStreak { get; init; }
        internal int PeakWinStreak { get; init; }
        internal IReadOnlyList<PvpJobScore> Jobs { get; init; } = Array.Empty<PvpJobScore>();
    }
}
