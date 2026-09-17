using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;
using PvfLib;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{


	private readonly ICharacterRepository _characterRepository;

	private readonly ISessionDirectory _sessions;

	private readonly RaidManager _raids;

	private readonly ConcurrentDictionary<Guid, byte> _objectSent = new ConcurrentDictionary<Guid, byte>();

	private readonly ConcurrentDictionary<string, Guid> _timerVersions = new ConcurrentDictionary<string, Guid>();

	private readonly ConcurrentDictionary<(uint RaidId, uint SymbolId), uint> _symbolValues = new ConcurrentDictionary<(uint, uint), uint>();

	private readonly ConcurrentDictionary<uint, uint> _infectionDungeonByRaid = new ConcurrentDictionary<uint, uint>();

	private readonly ConcurrentDictionary<uint, byte> _blackVolcanoBarrierBroken = new ConcurrentDictionary<uint, byte>();

	private readonly ConcurrentDictionary<uint, object> _raidRuntimeLocks = new ConcurrentDictionary<uint, object>();

	private readonly ConcurrentDictionary<uint, PhaseRewardFlow> _phaseRewardFlows = new ConcurrentDictionary<uint, PhaseRewardFlow>();

	private readonly ConcurrentDictionary<(uint RaidId, byte BuffType), AntonRaidBuffActivation> _raidBuffActivations = new ConcurrentDictionary<(uint, byte), AntonRaidBuffActivation>();

	private readonly ConcurrentDictionary<(uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId), uint[]> _raidMonsterRuntimeValues = new ConcurrentDictionary<(uint, ushort, uint, uint), uint[]>();

	public RaidHandler(ICharacterRepository characterRepository, ISessionDirectory sessions, RaidManager raids)
	{
		_characterRepository = characterRepository ?? throw new ArgumentNullException("characterRepository");
		_sessions = sessions ?? throw new ArgumentNullException("sessions");
		_raids = raids ?? throw new ArgumentNullException("raids");
	}

	private static void RunInBackground(Task task, string operation)
	{
		_ = ObserveBackgroundTaskAsync(task, operation);
	}

	private static async Task ObserveBackgroundTaskAsync(Task task, string operation)
	{
		try
		{
			await task;
		}
		catch (Exception value)
		{
			FileLogger.Log($"[GameProtocol] RAID_BACKGROUND_TASK operation={operation} error={value}");
		}
	}

	private static bool IsRaidSession(EnhancedClientSession session)
	{
		if (session != null)
		{
			return GameNetworkConfig.IsRaidListener(session.ListenerPort);
		}
		return false;
	}

	public async Task HandleCreateRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryBuildMember(session, out var member) || !TryReadTitle(body, out var title))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		if (title.Length == 0)
		{
			title = BuildDefaultTitle(member.CharacterId);
		}
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		RaidSnapshot raid = _raids.Create(title, member, channelId);
		await SendRaidObjectAsync(session, raid);
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		foreach (EnhancedClientSession raidChannelSession in GetRaidChannelSessions(raid))
		{
			if (raidChannelSession.SessionId != session.SessionId)
			{
				await SyncRaidViewerUserInfoAsync(raidChannelSession, raid);
				await raidChannelSession.SendPacketAsync(BuildRaidObjectPacketForRecipient(raidChannelSession, raid, members));
			}
		}
		_objectSent[session.SessionId] = 0;
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, RaidPacketBuilder.BuildCreateAck(raid.RaidId)));
		await BroadcastRaidDirectoryAsync(raid);
		FileLogger.Log($"[GameProtocol] CREATE_RAID channel={channelId} cid={member.CharacterId} user={member.UserId} raid={raid.RaidId} title={BitConverter.ToString(raid.TitleBytes)}");
	}

	internal static bool ShouldRejectRaidWaitingListRequest(uint raidState, byte stage)
	{
		// Stage 0 is also the attack status-window refresh request.
		return false;
	}


	public async Task HandleLeaveRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		RaidLeaveResult result = _raids.Leave(userId);
		if (!result.Ok)
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		_objectSent.TryRemove(session.SessionId, out var _);
		if (result.Disbanded)
		{
			ClearDisbandedRaidState(result.PreviousRaid);
		}
		try
		{
			await SendAckAsync(session, header.type, success: true);
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidRemove(result.RaidId)));
		}
		finally
		{
			await BroadcastRaidDepartureAsync(result.PreviousRaid);
		}
		FileLogger.Log($"[GameProtocol] LEAVE_RAID user={userId} raid={result.RaidId} disbanded={result.Disbanded}");
	}


	public async Task HandleDungeonAbortedAsync(EnhancedClientSession session, int dungeonId, string reason)
	{
		if (IsAntonRaidDungeon(dungeonId) && TryResolveUserId(session, out var userId) && _raids.TryAbandonDungeon(userId, (uint)dungeonId, out var raid, out var memberKeys))
		{
			ResetRaidMonsterRuntimeValues(raid, userId, (uint)dungeonId);
			await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo((uint)dungeonId, 0u, memberKeys));
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ABORT raid={raid.RaidId} dungeon={dungeonId} reason={reason} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	public async Task HandleRaidDoBehavior(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		bool ok = IsRaidDoBehaviorRequest(body) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 2;
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_DO_BEHAVIOR rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_DO_BEHAVIOR, body);
		FileLogger.Log($"[GameProtocol] RAID_DO_BEHAVIOR relayed raid={raid.RaidId} user={userId} target={BitConverter.ToUInt32(body, 0)} behavior={BitConverter.ToUInt32(body, 4)}");
	}

	internal static bool IsRaidDoBehaviorRequest(byte[] body)
	{
		if (body != null)
		{
			return body.Length == 8;
		}
		return false;
	}

	public async Task HandleRaidSetSymbol(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		bool ok = TryReadRaidSetSymbolRequest(body, out var symbolId, out var operand, out var operation) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 2 && raid.PhaseIndex == 1 && symbolId == 110 && _symbolValues.ContainsKey((raid.RaidId, symbolId));
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_SET_SYMBOL rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		await ChangeBlackVolcanoBarrierAsync(raid, operand, operation, "pvf-symbol-request");
		FileLogger.Log($"[GameProtocol] RAID_SET_SYMBOL applied raid={raid.RaidId} user={userId} symbol={symbolId} operation={operation} operand={operand}");
	}

	internal static bool TryReadRaidSetSymbolRequest(byte[] body, out uint symbolId, out uint operand, out byte operation)
	{
		symbolId = 0u;
		operand = 0u;
		operation = byte.MaxValue;
		if (body == null || body.Length != 9)
		{
			return false;
		}
		symbolId = BitConverter.ToUInt32(body, 0);
		operand = BitConverter.ToUInt32(body, 4);
		operation = body[8];
		return operation <= 2;
	}

	internal static bool TryApplyRaidSymbolOperation(uint currentValue, uint operand, byte operation, out uint nextValue)
	{
		switch (operation)
		{
		case 0:
			nextValue = operand;
			return true;
		case 1:
			nextValue = ((operand > (uint)(-1 - (int)currentValue)) ? uint.MaxValue : (currentValue + operand));
			return true;
		case 2:
			nextValue = ((operand < currentValue) ? (currentValue - operand) : 0u);
			return true;
		default:
			nextValue = currentValue;
			return false;
		}
	}

	public async Task HandleRaidManagerWork(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryReadRaidManagerWork(body, out var operation, out var targetActorId, out var partyIndex) || !TryResolveUserId(session, out var actingUserId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		FileLogger.Log($"[GameProtocol] RAID_MANAGER_WORK_RAW user={actingUserId} body={BitConverter.ToString(body)}");
		RaidSnapshot raid = null;
		bool ok = false;
		switch (operation)
		{
		case 1u:
		{
			if (_sessions.TryGet(session.Player.CharacterId, out var session2) && session2.SessionId == session.SessionId && _raids.TryGetByUser(actingUserId, out var raid3))
			{
				RaidMember raidMember = raid3.Members.FirstOrDefault((RaidMember m) => m.UserId == targetActorId);
				if (raidMember != null && _sessions.TryGet(checked((int)raidMember.CharacterId), out var session3) && session3.SessionId == raidMember.SessionId && session3.ListenerPort == session.ListenerPort && (session3.TcpClient?.Connected ?? false))
				{
					ok = _raids.TryTransferLeadership(actingUserId, session.SessionId, targetActorId, session3.SessionId, out raid);
				}
			}
			try
			{
				await SendAckAsync(session, header.type, ok);
			}
			finally
			{
				if (ok)
				{
					await BroadcastRaidLeaderChangedAsync(raid);
				}
			}
			FileLogger.Log($"[GameProtocol] RAID_TRANSFER_LEADER raid={raid?.RaidId} from={actingUserId} to={targetActorId} ok={ok}");
			return;
		}
		case 0u:
		{
			if (_raids.TryGetByUser(actingUserId, out var raid2))
			{
				if ((raid2.State == 2 || raid2.State == 5) && LivePartyAssignment != null)
				{
					raid = await LivePartyAssignment(session, raid2, targetActorId, (ushort)partyIndex);
					ok = raid != null;
				}
				else if (raid2.State == 0)
				{
					ok = _raids.TryAssignParty(actingUserId, targetActorId, partyIndex, out raid);
				}
			}
			break;
		}
		}
		await SendAckAsync(session, header.type, ok);
		if (ok)
		{
			await BroadcastRaidObjectAsync(raid);
			await BroadcastRaidMembersAsync(raid);
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_MANAGER_WORK raid={raid.RaidId} user={actingUserId} actor={targetActorId} partyIndex={partyIndex}");
		}
	}

	public async Task HandleModifyRaidInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId) || !TryReadTitle(body, out var titleBytes) || !_raids.TryUpdateTitle(userId, titleBytes, out var raid))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		await SendAckAsync(session, header.type, success: true);
		await BroadcastRaidInfoAsync(raid);
		FileLogger.Log($"[GameProtocol] MODIFY_RAID_INFO raid={raid.RaidId} user={userId} title={BitConverter.ToString(titleBytes)}");
	}

	public Task<bool> TryHandleCreatePopupClose(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !IsCreatePopupCloseBody(body))
		{
			return Task.FromResult(result: false);
		}
		FileLogger.Log($"[GameProtocol] CLOSE_RAID_CREATE_POPUP session={session.SessionId} body={BitConverter.ToString(body)}");
		return Task.FromResult(result: true);
	}

	public static bool IsCreatePopupCloseBody(byte[] body)
	{
		if (body != null && body.Length == 3 && body[0] == 1)
		{
			return BitConverter.ToUInt16(body, 1) == 665;
		}
		return false;
	}

	private async Task SendRaidObjectAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		await SyncRaidViewerUserInfoAsync(session, raid);
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		await session.SendPacketAsync(BuildRaidObjectPacketForRecipient(session, raid, members));
	}

	private Task SendRaidStateValueAsync(EnhancedClientSession session, uint state, uint stateArgument)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_STATE, RaidPacketBuilder.BuildRaidState(state, stateArgument)));
	}

	private async Task BroadcastRaidObjectAsync(RaidSnapshot raid)
	{
		if (raid.State != 0)
		{
			await BroadcastRaidMemberUserInfoToChannelAsync(raid);
			await BroadcastRaidInfoAsync(raid);
			IReadOnlyList<RaidMemberSnapshot> activeMembers = ToPacketMembers(raid);
			await BroadcastToRaidChannelAsync(raid, (EnhancedClientSession recipient) => BuildRaidMembersPacketForRecipient(recipient, raid.RaidId, activeMembers));
		}
		else
		{
			await BroadcastRaidMemberUserInfoToChannelAsync(raid);
			IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
			await BroadcastToRaidChannelAsync(raid, (EnhancedClientSession recipient) => BuildRaidObjectPacketForRecipient(recipient, raid, members));
		}
	}

	private Task BroadcastRaidStateAsync(RaidSnapshot raid)
	{
		byte[] packet = ((raid.State == 4 && raid.StateArgument == 1) ? BuildFailedRaidResultPacket(raid) : GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_STATE, RaidPacketBuilder.BuildRaidState(raid.State, raid.StateArgument)));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidNotificationAsync(RaidSnapshot raid, NotiPacketTypeA21 type, byte[] body)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, (ushort)type, body);
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidInfoAsync(RaidSnapshot raid)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidInfoUpdate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader)));
		return BroadcastToRaidChannelAsync(raid, packet);
	}

	private async Task BroadcastRaidMembersAsync(RaidSnapshot raid)
	{
		await BroadcastRaidMemberUserInfoToChannelAsync(raid);
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		await BroadcastToRaidChannelAsync(raid, (EnhancedClientSession recipient) => BuildRaidMembersPacketForRecipient(recipient, raid.RaidId, members));
	}

	private static Task SendAckAsync(EnhancedClientSession session, ushort type, bool success)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, type, new byte[1] { success ? ((byte)1) : ((byte)0) }));
	}

	private bool TryBuildMember(EnhancedClientSession session, out RaidMember member)
	{
		member = null;
		int item = SessionOwnerResolver.Resolve(session).characterId;
		if (item <= 0 || item > 65535)
		{
			return false;
		}
		ushort userId = ((session.Player != null && session.Player.UserId != 0) ? session.Player.UserId : ((ushort)item));
		CharacterRecord byId = _characterRepository.GetById(item);
		member = new RaidMember
		{
			UserId = userId,
			CharacterId = (uint)item,
			SessionId = session.SessionId,
			NameBytes = (byId?.Name ?? session.Player?.Name ?? Array.Empty<byte>()),
			Job = (byId?.Job ?? session.Player?.Job ?? 0),
			GrowType = (byId?.GrowType ?? session.Player?.GrowType ?? 0)
		};
		return true;
	}

	private static bool TryResolveUserId(EnhancedClientSession session, out ushort userId)
	{
		if (session.Player != null && session.Player.UserId != 0)
		{
			userId = session.Player.UserId;
			return true;
		}
		int item = SessionOwnerResolver.Resolve(session).characterId;
		if (item > 0 && item <= 65535)
		{
			userId = (ushort)item;
			return true;
		}
		userId = 0;
		return false;
	}

	internal static bool TryReadTitle(byte[] body, out byte[] title)
	{
		title = Array.Empty<byte>();
		if (body == null || body.Length < 5)
		{
			return false;
		}
		int num = BitConverter.ToInt32(body, 1);
		if (num < 0 || num > body.Length - 1 - 4)
		{
			return false;
		}
		title = new byte[num];
		Buffer.BlockCopy(body, 5, title, 0, num);
		return true;
	}

	private static byte[] BuildDefaultTitle(uint characterId)
	{
		return ClientTextEncoding.GetBytes($"Raid: {characterId}");
	}


	private static RaidMemberSnapshot ToPacketMember(RaidMember member)
	{
		return new RaidMemberSnapshot
		{
			UserId = member.UserId,
			CharacterId = member.CharacterId,
			NameBytes = member.NameBytes,
			Job = member.Job,
			GrowType = member.GrowType,
			PartyIndex = member.PartyIndex
		};
	}

	private static IReadOnlyList<RaidMemberSnapshot> ToPacketMembers(RaidSnapshot raid)
	{
		List<RaidMemberSnapshot> list = new List<RaidMemberSnapshot>(raid.Members.Count);
		foreach (RaidMember member in raid.Members)
		{
			RaidMemberSnapshot raidMemberSnapshot = ToPacketMember(member);
			raidMemberSnapshot.NameBytes = BuildRaidMemberDisplayName(member, raid.State);
			raidMemberSnapshot.PhaseClearCount = member.PhaseClearCount;
			raidMemberSnapshot.PhaseIndex = checked((byte)raid.PhaseIndex);
			list.Add(raidMemberSnapshot);
		}
		return list;
	}

	private static IEnumerable<int> ToCharacterIds(RaidSnapshot raid)
	{
		foreach (RaidMember member in raid.Members)
		{
			yield return checked((int)member.CharacterId);
		}
	}

	public async Task ClearSessionAsync(Guid sessionId)
	{
		_objectSent.TryRemove(sessionId, out var _);
		RaidLeaveResult raidLeaveResult = _raids.OnSessionDisconnected(sessionId);
		if (raidLeaveResult.Ok)
		{
			if (raidLeaveResult.Disbanded)
			{
				ClearDisbandedRaidState(raidLeaveResult.PreviousRaid);
			}
			await BroadcastRaidDepartureAsync(raidLeaveResult.PreviousRaid);
		}
	}

	private void ClearDisbandedRaidState(RaidSnapshot raid)
	{
		if (_raids.TryGetByRaidId(raid.RaidId, out var raid2) && raid2.InstanceId != raid.InstanceId)
		{
			return;
		}
		foreach (RaidMember member in raid.Members)
		{
			_objectSent.TryRemove(member.SessionId, out var _);
		}
		CleanupRaidRuntimeState(raid.RaidId);
	}

	private Task BroadcastRaidDepartureAsync(RaidSnapshot previousRaid)
	{
		int channelId = (int)(previousRaid.RaidId >> 16);
		return Task.WhenAll(from recipient in GetRaidChannelSessions(channelId)
			select SessionDirectory.TrySendBestEffortAsync(async delegate(CancellationToken cancellationToken)
			{
				if (!_raids.TryGetByRaidId(previousRaid.RaidId, out var raid))
				{
					await recipient.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidRemove(previousRaid.RaidId)), cancellationToken);
				}
				else if (raid.InstanceId == previousRaid.InstanceId)
				{
					await recipient.SendPacketAsync(BuildRaidMembersPacketForRecipient(recipient, raid.RaidId, ToPacketMembers(raid)), cancellationToken);
				}
				await recipient.SendPacketAsync(BuildRaidDirectoryPacket(ToDirectoryEntries(_raids.ListRaidsByChannel(channelId))), cancellationToken);
			}, $"raid departure raid={previousRaid.RaidId} recipient={recipient.SessionId}"));
	}
}
