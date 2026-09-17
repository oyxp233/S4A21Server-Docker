namespace DfoServer.Game.Guilds
{
    internal enum GuildResult
    {
        Success, InvalidName, InvalidPromotion, DuplicateName, AlreadyMember,
        MissingCharacter, InsufficientGold, NotFound, NotLeader, NotApplicant,
        LeaderCannotLeave, StaleSession, PersistenceFailed, InvalidRank
    }
    internal sealed record GuildRecord(int Id, string Name, string Promotion, int LeaderId, string Announcement = "");
    internal sealed record GuildCreateResult(GuildResult Status, GuildRecord Guild = null, int GoldAfter = 0);
    internal sealed record GuildMemberRecord(int CharacterId, int AccountId, string Name,
        ushort Level, byte Job, byte GrowType, bool IsLeader, uint OfflineSeconds, byte MemberRank = 4, string Memo = "")
    {
        internal byte Rank => IsLeader ? (byte)1 : MemberRank;
    }
    internal sealed record GuildRoster(GuildRecord Guild, string LeaderName,
        System.Collections.Generic.IReadOnlyList<GuildMemberRecord> Members);
    internal sealed record GuildSearchResult(GuildRecord Guild, ushort MemberCount);
    internal sealed record GuildApplication(int CharacterId, string Name, byte Job, byte GrowType,
        byte Level, string Message, uint ElapsedSeconds);
    internal sealed record GuildPendingApplication(GuildRecord Guild, string Message);
}
