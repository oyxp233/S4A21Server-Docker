namespace DfoServer.Game.Dungeon
{
    public enum DungeonInstanceState
    {
        Created = 0,
        Active = 1,
        Cleared = 2,
        Ending = 3,
        Ended = 4,
    }

    public enum DungeonRunState
    {
        None = 0,
        Created = 1,
        Selecting = 2,
        Active = 3,
        ClearCommitting = 4,
        Cleared = 5,
        Ending = 6,
        Ended = 7,
    }

    public enum DungeonRunEndReason
    {
        ReturnToTown = 0,
        DeathRespawn = 1,
        TutorialExit = 2,
        PartyLeave = 3,
        SessionTeardown = 4,
        CharacterSwitch = 5,
        ReplacedByNewRun = 6,
        EntryRejected = 7,
    }

    public enum DungeonSettlementState
    {
        NotStarted = 0,
        Preparing = 1,
        ResultShown = 2,
        CardsRevealed = 3,
        Completed = 4,
    }

    public enum DungeonRoomState
    {
        Created = 0,
        Active = 1,
        Cleared = 2,
        Closed = 3,
    }

    public enum DungeonEncounterState
    {
        NotStarted = 0,
        Active = 1,
        Succeeded = 2,
        Failed = 3,
    }
}
