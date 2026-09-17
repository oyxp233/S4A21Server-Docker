using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Friends;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	internal static byte[] BuildRaidObjectPacketForRecipient(EnhancedClientSession recipient, RaidSnapshot raid, IReadOnlyList<RaidMemberSnapshot> members)
	{
		bool flag = (raid.State == 3 && raid.StateArgument == 0 && raid.PhaseIndex == 1) || (raid.State == 4 && ((raid.StateArgument == 0 && raid.PhaseIndex == 1) || (raid.StateArgument == 1 && raid.PhaseIndex == 0)));
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader), members, recipient.SupportsRaidMemberColumnV1 && !flag));
	}

	internal static byte[] BuildRaidMembersPacketForRecipient(EnhancedClientSession recipient, uint raidId, IReadOnlyList<RaidMemberSnapshot> members)
	{
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidMembersUpdate(raidId, members, recipient.SupportsRaidMemberColumnV1));
	}

	private IEnumerable<EnhancedClientSession> GetRaidChannelSessions(RaidSnapshot raid)
	{
		int channelId = (int)(raid.RaidId >> 16);
		foreach (EnhancedClientSession allGameSession in _sessions.GetAllGameSessions())
		{
			if (allGameSession != null && GameNetworkConfig.IsRaidListener(allGameSession.ListenerPort) && GameNetworkConfig.ResolveGameChannel(allGameSession.ListenerPort).ChannelId == channelId)
			{
				yield return allGameSession;
			}
		}
	}

	private Task BroadcastToRaidChannelAsync(RaidSnapshot raid, byte[] packet)
	{
		List<Task> list = new List<Task>();
		foreach (EnhancedClientSession raidChannelSession in GetRaidChannelSessions(raid))
		{
			list.Add(raidChannelSession.SendPacketAsync(packet));
		}
		return Task.WhenAll(list);
	}

	private Task BroadcastToRaidChannelAsync(RaidSnapshot raid, Func<EnhancedClientSession, byte[]> packetForRecipient)
	{
		return SendRaidPacketsByRecipientAsync(GetRaidChannelSessions(raid), packetForRecipient, (EnhancedClientSession recipient, byte[] packet) => recipient.SendPacketAsync(packet));
	}

	internal static Task SendRaidPacketsByRecipientAsync(IEnumerable<EnhancedClientSession> recipients, Func<EnhancedClientSession, byte[]> packetForRecipient, Func<EnhancedClientSession, byte[], Task> sendPacket)
	{
		List<Task> list = new List<Task>();
		foreach (EnhancedClientSession recipient in recipients)
		{
			list.Add(sendPacket(recipient, packetForRecipient(recipient)));
		}
		return Task.WhenAll(list);
	}

	private Task BroadcastRaidDirectoryAsync(RaidSnapshot raid)
	{
		byte[] packet = BuildRaidDirectoryPacket(ToDirectoryEntries(_raids.ListRaidsByChannel((int)(raid.RaidId >> 16))));
		return BroadcastToRaidChannelAsync(raid, packet);
	}

	private Task SyncRaidViewerUserInfoAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		if (session == null || raid == null)
		{
			return Task.CompletedTask;
		}
		return SendUserInfoContextToRecipientAsync(session, raid.Members);
	}

	private Task BroadcastRaidMemberUserInfoToChannelAsync(RaidSnapshot raid)
	{
		if (raid == null)
		{
			return Task.CompletedTask;
		}
		List<Task> list = new List<Task>();
		foreach (EnhancedClientSession raidChannelSession in GetRaidChannelSessions(raid))
		{
			list.Add(SendUserInfoContextToRecipientAsync(raidChannelSession, raid.Members));
		}
		return Task.WhenAll(list);
	}

	private async Task SendUserInfoContextToRecipientAsync(EnhancedClientSession recipient, IEnumerable<RaidMember> members)
	{
		if (recipient == null || members == null)
		{
			return;
		}
		HashSet<ushort> hashSet = new HashSet<ushort>();
		List<Task> list = new List<Task>();
		foreach (RaidMember member in members)
		{
			if (member == null || member.UserId == 0 || !hashSet.Add(member.UserId) || !_sessions.TryGet((int)member.CharacterId, out var session) || session?.Player == null || session.SessionId != member.SessionId)
			{
				continue;
			}
			byte[] context = BuildRaidFormationUserContextPacket(session);
			if (context != null)
			{
				list.Add(SessionDirectory.TrySendBestEffortAsync((CancellationToken cancellationToken) => recipient.SendPacketAsync(context, cancellationToken), $"raid user context recipientUid={recipient?.Player?.UserId:X4} sourceUid={member.UserId:X4}"));
			}
		}
		if (list.Count > 0)
		{
			await Task.WhenAll(list);
		}
	}

	internal bool TryPrepareRaidMemberColumnView(EnhancedClientSession session, byte[] body, out RaidSnapshot raid, out IReadOnlyList<byte[]> packets)
	{
		raid = null;
		packets = Array.Empty<byte[]>();
		if (!RaidPacketBuilder.TryReadMemberColumnRequest(body, out var raidId) || !IsRaidSession(session) || !TryResolveUserId(session, out var _) || !GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var channel) || channel.ChannelId != (int)(raidId >> 16))
		{
			return false;
		}
		int item = SessionOwnerResolver.Resolve(session).characterId;
		if (item <= 0 || !_sessions.TryGet(item, out var session2) || session2 == null || session2.SessionId != session.SessionId || !_raids.TryGetByRaidId(raidId, out var raid2) || raid2.Leader == null || raid2.PhaseIndex > 1 || raid2.Members.Count > 20)
		{
			return false;
		}
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid2);
		byte[] array = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidInfoUpdate(raid2.RaidId, raid2.TitleBytes, raid2.State, raid2.StateArgument, ToPacketMember(raid2.Leader)));
		byte[] array2 = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidMembersUpdate(raid2.RaidId, members, columnV1: true));
		if (!session.TrySetRaidMemberColumnProtocolVersion(1u))
		{
			return false;
		}
		raid = raid2;
		packets = new byte[2][] { array, array2 };
		return true;
	}

	internal static byte[] BuildRaidFormationUserContextPacket(EnhancedClientSession source)
	{
		if (source?.Player == null || source.Player.UserId == 0)
		{
			return null;
		}
		byte[] body = UserInfoSubtype0Builder.BuildNotificationBody(UnitedFriendSystem.BuildUserInfoRecord(source.Player));
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.USERINFO, body);
	}

	private async Task SendRaidDirectoryAsync(EnhancedClientSession session, int channelId)
	{
		IReadOnlyList<RaidSnapshot> raids = _raids.ListRaidsByChannel(channelId);
		foreach (RaidSnapshot item in raids)
		{
			if (item.State == 0 || !_objectSent.ContainsKey(session.SessionId) || !item.Members.Any((RaidMember member) => member.SessionId == session.SessionId))
			{
				await SendRaidObjectAsync(session, item);
			}
			else
			{
				await ResendRaidStateToSessionAsync(session, item);
			}
		}
		await session.SendPacketAsync(BuildRaidDirectoryPacket(ToDirectoryEntries(raids)));
	}

	internal static byte[] BuildRaidDirectoryPacket(IReadOnlyList<RaidDirectoryEntry> entries)
	{
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_LIST, RaidPacketBuilder.BuildRaidDirectory(entries));
	}

	private IEnumerable<EnhancedClientSession> GetRaidChannelSessions(int channelId)
	{
		foreach (EnhancedClientSession allGameSession in _sessions.GetAllGameSessions())
		{
			if (allGameSession != null && GameNetworkConfig.IsRaidListener(allGameSession.ListenerPort) && GameNetworkConfig.ResolveGameChannel(allGameSession.ListenerPort).ChannelId == channelId)
			{
				yield return allGameSession;
			}
		}
	}

	private static IReadOnlyList<RaidDirectoryEntry> ToDirectoryEntries(IReadOnlyList<RaidSnapshot> raids)
	{
		List<RaidDirectoryEntry> list = new List<RaidDirectoryEntry>(raids.Count);
		foreach (RaidSnapshot raid in raids)
		{
			RaidMember leader = raid.Leader;
			if (leader != null)
			{
				list.Add(new RaidDirectoryEntry
				{
					RaidId = raid.RaidId,
					TitleBytes = raid.TitleBytes,
					State = raid.State,
					StateArgument = raid.StateArgument,
					Leader = ToPacketMember(leader),
					MemberCount = raid.Members.Count
				});
			}
		}
		return list;
	}

	internal static byte[] BuildRaidMemberDisplayName(RaidMember member, uint state)
	{
		return (byte[])member.NameBytes.Clone();
	}
}
