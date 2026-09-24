using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	public Func<EnhancedClientSession, EnhancedClientSession, int, Task<bool>> RaidPeerRequestAsync { get; set; }

	public async Task HandleSetRaidWaiting(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryBuildMember(session, out var member) || _raids.TryGetByUser(member.UserId, out var _))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		_raids.TryAddWaiting(channelId, member);
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, RaidPacketBuilder.BuildRaidWaitingAck()));
		FileLogger.Log($"[GameProtocol] RAID_WAITING_JOIN channel={channelId} user={member.UserId} cid={member.CharacterId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
	}

	public async Task HandleRaidWaitingListRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (IsRaidSession(session))
		{
			GamePacketWriter gamePacketWriter = new GamePacketWriter();
			gamePacketWriter.WriteUInt32(0u);
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, gamePacketWriter.ToArray()));
			FileLogger.Log("[GameProtocol] RAID_RECENT_LIST_0334 body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
		}
	}

	private async Task SendWaitingPlayerListAsync(EnhancedClientSession session, int channelId)
	{
		List<RaidWaitingPlayerSnapshot> players = new List<RaidWaitingPlayerSnapshot>();
		foreach (RaidMember waiting in _raids.GetWaitingList(channelId))
		{
			if (_sessions.TryGet(checked((int)waiting.CharacterId), out var session2) && IsRaidSession(session2) && GameNetworkConfig.ResolveGameChannel(session2.ListenerPort).ChannelId == channelId && !_raids.TryGetByUser(waiting.UserId, out var _) && TryProjectWaitingPlayer(waiting, session2.SessionId, session2.Player, channelId, out var player))
			{
				players.Add(player);
			}
		}
		await session.SendPacketAsync(RaidWaitingPacketBuilder.Build(players));
		FileLogger.Log($"[GameProtocol] RAID_WAITING_SENT channel={channelId} rows={players.Count}");
	}

	internal static bool TryProjectWaitingPlayer(RaidMember registered, Guid sessionId, PlayerContext current, int channelId, out RaidWaitingPlayerSnapshot player)
	{
		player = null;
		if (registered == null || current == null || sessionId != registered.SessionId || current.CharacterId != registered.CharacterId || current.UserId != registered.UserId || current.Name == null || current.Name.Length == 0 || current.Name.Length >= 30 || Array.IndexOf(current.Name, (byte)0) >= 0)
		{
			return false;
		}
		player = new RaidWaitingPlayerSnapshot
		{
			UserId = current.UserId,
			ChannelId = checked((byte)channelId),
			ServerIndex = 1,
			Level = current.Level,
			Job = current.Job,
			GrowType = current.GrowType,
			NameBytes = (byte[])current.Name.Clone()
		};
		return true;
	}

	private bool IsRaidPeerReady(EnhancedClientSession session)
	{
		if (session?.Player != null && IsRaidSession(session) && session.Player.TownPresenceReady && session.Player.UserState == 0 && session.Player.CurrentRun == null && _sessions.TryGet(session.Player.CharacterId, out var session2))
		{
			return session == session2;
		}
		return false;
	}

	internal bool TryCreateRaidPeerRequest(EnhancedClientSession requester, EnhancedClientSession recipient, out RaidSnapshot raid, out ushort joinerUserId)
	{
		raid = null;
		joinerUserId = 0;
		if (!IsRaidPeerReady(requester) || !IsRaidPeerReady(recipient) || requester.ListenerPort != recipient.ListenerPort || !TryResolveUserId(requester, out var userId) || !TryResolveUserId(recipient, out var userId2) || userId == userId2)
		{
			return false;
		}
		if (_raids.TryGetJoinableRaid(userId, requester.SessionId, userId2, out raid))
		{
			joinerUserId = userId2;
		}
		else if (_raids.TryGetJoinableRaid(userId2, recipient.SessionId, userId, out raid))
		{
			joinerUserId = userId;
		}
		return raid != null;
	}

	internal async Task<bool> HandleRaidPeerAcceptAsync(EnhancedClientSession accepterSession, EnhancedClientSession inviterSession, RaidSnapshot expected, ushort joinerUserId)
	{
		if (!IsRaidPeerReady(accepterSession) || !IsRaidPeerReady(inviterSession) || expected?.Leader == null || accepterSession.ListenerPort != inviterSession.ListenerPort)
		{
			return false;
		}
		EnhancedClientSession joinerSession = ((inviterSession.Player.UserId == joinerUserId) ? inviterSession : accepterSession);
		EnhancedClientSession enhancedClientSession = ((joinerSession == inviterSession) ? accepterSession : inviterSession);
		if (joinerSession.Player.UserId != joinerUserId || enhancedClientSession.Player.UserId != expected.LeaderUserId || enhancedClientSession.SessionId != expected.Leader.SessionId || !TryBuildMember(joinerSession, out var member))
		{
			return false;
		}
		RaidSnapshot joined = TryJoinConfirmedRaid(expected, member);
		if (joined == null)
		{
			return false;
		}
		_raids.TryRemoveWaiting(member.UserId);
		_objectSent[joinerSession.SessionId] = 0;
		await SendRaidObjectAsync(joinerSession, joined);
		await ResendRaidStateToSessionAsync(joinerSession, joined);
		await BroadcastRaidObjectAsync(joined);
		await BroadcastRaidMembersAsync(joined);
		await BroadcastRaidDirectoryAsync(joined);
		await joinerSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice("您已加入攻坚队。", 0)));
		string text = ClientTextEncoding.GetString(member.NameBytes);
		byte[] joinNotice = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice("玩家[" + text + "]加入攻坚队", 0));
		foreach (RaidMember member2 in joined.Members)
		{
			if (member2.UserId != member.UserId)
			{
				await _sessions.SendToAsync(checked((int)member2.CharacterId), joinNotice);
			}
		}
		FileLogger.Log($"[GameProtocol] RAID_PEER_ACCEPT raid={joined.RaidId} joiner={member.UserId} cid={member.CharacterId}");
		return true;
	}

	private async Task NotifyRecruitingLeaderAsync(int channelId, RaidMember applicant, string action)
	{
		if (_raids.TryFindRecruitingRaid(channelId, out var raid))
		{
			RaidMember leader = raid.Leader;
			if (leader != null)
			{
				string text = ClientTextEncoding.GetString(applicant.NameBytes);
				string message = "玩家[" + text + "]" + action;
				await _sessions.SendToAsync(checked((int)leader.CharacterId), GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice(message, 0)));
			}
		}
	}

	public async Task HandleRaidJoinRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session))
		{
			return;
		}
		uint targetCharacterId = 0u;
		if (body != null && body.Length == 3)
		{
			targetCharacterId = BitConverter.ToUInt16(body, 1);
		}
		if (targetCharacterId == 0 || targetCharacterId > 65535 || !_sessions.TryGet(checked((int)targetCharacterId), out var session2) || !IsRaidSession(session2))
		{
			FileLogger.Log($"[GameProtocol] RAID_0364 rejected target={targetCharacterId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
			return;
		}
		bool flag = RaidPeerRequestAsync != null;
		if (flag)
		{
			flag = await RaidPeerRequestAsync(session, session2, 0);
		}
		bool flag2 = flag;
		TryResolveUserId(session, out var userId);
		bool flag3 = _raids.TryGetByUser(userId, out var _);
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		if (flag2 && !flag3 && TryBuildMember(session, out var member))
		{
			_raids.TryAddWaiting(channelId, member);
		}
		FileLogger.Log($"[GameProtocol] RAID_0364 channel={channelId} user={userId} target={targetCharacterId} inviting={flag3} prompted={flag2}");
	}

	private RaidSnapshot TryJoinConfirmedRaid(RaidSnapshot expected, RaidMember member)
	{
		if (!_raids.TryGetByRaidId(expected.RaidId, out var raid) || raid.InstanceId != expected.InstanceId)
		{
			return null;
		}
		checked
		{
			int num = _characterRepository.GetById((int)member.CharacterId)?.AccountId ?? 0;
			if (num > 0)
			{
				bool flag = false;
				foreach (RaidMember member2 in raid.Members)
				{
					if ((_characterRepository.GetById((int)member2.CharacterId)?.AccountId ?? 0) == num)
					{
						flag = true;
						break;
					}
				}
				if (flag)
				{
					FileLogger.Log($"[GameProtocol] RAID_JOIN rejected same-account raid={raid.RaidId} user={member.UserId} account={num}");
					return null;
				}
			}
			if (!_raids.TryAddConfirmedMember(expected, member, out var raid2))
			{
				return null;
			}
			return raid2;
		}
	}

	public async Task HandleRaidRequestMembers(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId;
		RaidSnapshot raid;
		if (body == null || body.Length != 4)
		{
			if (session == null)
			{
				return;
			}
			await session.SendPreparedPacketBatchAsync(delegate
			{
				if (!TryPrepareRaidMemberColumnView(session, body, out var raid2, out var packets))
				{
					return Array.Empty<byte[]>();
				}
				FileLogger.Log($"[GameProtocol] RAID_MEMBER_COLUMN_VIEW raid={raid2.RaidId} session={session.SessionId} version=1");
				return packets;
			});
		}
		else if (IsRaidSession(session) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid))
		{
			_raids.RebindSession(userId, session.SessionId);
			if (!_objectSent.ContainsKey(session.SessionId))
			{
				_objectSent[session.SessionId] = 0;
				await SendRaidObjectAsync(session, raid);
			}
			await ResendRaidStateToSessionAsync(session, raid);
			FileLogger.Log($"[GameProtocol] RAID_REQUEST_MEMBERS raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
		}
	}

	public async Task HandleRejoinRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (IsRaidSession(session) && TryResolveUserId(session, out var userId))
		{
			_raids.RebindSession(userId, session.SessionId);
			if (_raids.TryGetByUser(userId, out var raid))
			{
				_objectSent[session.SessionId] = 0;
				await SendRaidObjectAsync(session, raid);
				await ResendRaidStateToSessionAsync(session, raid);
				FileLogger.Log($"[GameProtocol] REJOIN_RAID raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
			}
		}
	}

	public async Task HandleRebindResyncAsync(EnhancedClientSession session)
	{
		if (IsRaidSession(session) && TryResolveUserId(session, out var userId))
		{
			_raids.RebindSession(userId, session.SessionId);
			if (_raids.TryGetByUser(userId, out var raid) && !_objectSent.ContainsKey(session.SessionId))
			{
				_objectSent[session.SessionId] = 0;
				await SendRaidObjectAsync(session, raid);
				await ResendRaidStateToSessionAsync(session, raid);
				FileLogger.Log($"[GameProtocol] RAID_REBIND_RESYNC raid={raid.RaidId} user={userId} session={session.SessionId}");
			}
		}
	}

	public async Task HandleRaidChannelWelcomeAsync(EnhancedClientSession session)
	{
		if (IsRaidSession(session))
		{
			int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
			await SendRaidDirectoryAsync(session, channelId);
		}
	}

	public async Task HandleRaidOtherChannelList(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (IsRaidSession(session))
		{
			int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
			if (body != null && body.Length >= 1 && body[0] == 1)
			{
				await SendWaitingPlayerListAsync(session, channelId);
				FileLogger.Log($"[GameProtocol] RAID_OTHER_CHANNEL_LIST_WAITING channel={channelId} total={_raids.GetWaitingList(channelId).Count} body={BitConverter.ToString(body)}");
			}
			else
			{
				await SendRaidDirectoryAsync(session, channelId);
				FileLogger.Log($"[GameProtocol] RAID_OTHER_CHANNEL_LIST channel={channelId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
			}
		}
	}

	private async Task ResendRaidStateToSessionAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		await SyncRaidViewerUserInfoAsync(session, raid);
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidInfoUpdate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader))));
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		await session.SendPacketAsync(BuildRaidMembersPacketForRecipient(session, raid.RaidId, members));
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_ENTRY_COST_INFO, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(raid))));
		await SendRaidTimerSnapshotAsync(session, raid, DateTime.UtcNow);
		if (raid.State != 0)
		{
			await SendRaidClearCountsAsync(session, raid);
			await SendRaidBuffStatusAsync(session, raid);
			await SendRaidMonsterStatusAsync(session, raid);
		}
	}

	internal async Task HandleRaidTownAreaChangedAsync(EnhancedClientSession session, byte previousTownId, byte previousAreaId)
	{
		if (IsRaidSession(session) && TownHandler.ShouldNotifyRaidTownLoaded(session.ListenerPort, session.Player) && !Town.IsCeraRoom(session.Player.CurTownId, session.Player.CurAreaId) && (previousTownId != session.Player.CurTownId || previousAreaId != session.Player.CurAreaId) && !_raids.TryGetByUser(session.Player.UserId, out var _))
		{
			int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
			await SendRaidDirectoryAsync(session, channelId);
			FileLogger.Log($"[GameProtocol] RAID_TOWN_DIRECTORY_REFRESH channel={channelId} user={session.Player.UserId} from={previousTownId}:{previousAreaId} to={session.Player.CurTownId}:{session.Player.CurAreaId}");
		}
	}
}
