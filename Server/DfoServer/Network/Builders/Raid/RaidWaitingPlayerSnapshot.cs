using System;

namespace DfoServer.Network.Builders.Raid;

public sealed class RaidWaitingPlayerSnapshot
{
    public ushort UserId { get; init; }

    public byte ChannelId { get; init; }

    public byte ServerIndex { get; init; }

    public ushort Level { get; init; }

    public byte GrowType { get; init; }

    public byte Job { get; init; }

    public byte[] NameBytes { get; init; } = Array.Empty<byte>();
}
