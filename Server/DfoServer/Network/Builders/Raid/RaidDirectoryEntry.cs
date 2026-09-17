using System;

namespace DfoServer.Network.Builders.Raid;

public sealed class RaidDirectoryEntry
{
    public uint RaidId { get; set; }

    public byte[] TitleBytes { get; set; } = Array.Empty<byte>();

    public uint State { get; set; }

    public uint StateArgument { get; set; }

    public RaidMemberSnapshot Leader { get; set; }

    public int MemberCount { get; set; }
}
