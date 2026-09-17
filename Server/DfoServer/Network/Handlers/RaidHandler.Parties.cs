using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Party;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	internal Func<IReadOnlyList<RaidMember>, bool> PreparationPartiesReady { get; set; }

	internal Func<IReadOnlyList<RaidMember>, IReadOnlyList<RaidMember>> PreparationPartyOrder { get; set; }

	internal Func<EnhancedClientSession, RaidSnapshot, ushort, ushort, Task<RaidSnapshot>> LivePartyAssignment { get; set; }

	internal bool TryCommitPartyJoin(PartyMember inviter, PartyMember invitee, Action<IReadOnlyList<RaidMember>> commit)
	{
		return _raids.TryCommitPartyJoin(inviter, invitee, commit);
	}

	internal bool TryCommitPartySettings(ushort userId, Guid sessionId, Action<IReadOnlyList<RaidMember>> commit)
	{
		return _raids.TryCommitPartySettings(userId, sessionId, commit);
	}

	internal bool CommitLivePartyAssignment(RaidSnapshot expected, ushort actor, Guid session, ushort target, ushort index, Func<IReadOnlyList<RaidMember>, bool> commit, out RaidSnapshot result)
	{
		return _raids.TryAssignLiveParty(expected, actor, session, target, index, commit, out result);
	}

	internal bool TryCommitPreparationResponse(ushort leader, Guid leaderSession, ushort member, Guid memberSession, Func<IReadOnlyList<RaidMember>, bool> commit)
	{
		return _raids.TryCommitPreparationResponse(leader, leaderSession, member, memberSession, commit);
	}

	internal static bool TryReadRaidManagerWork(byte[] body, out uint operation, out ushort targetUserId, out uint partyIndex)
	{
		operation = 0u;
		targetUserId = 0;
		partyIndex = 0u;
		if (body == null || body.Length != 12)
		{
			return false;
		}
		operation = BitConverter.ToUInt32(body, 0);
		uint num = BitConverter.ToUInt32(body, 4);
		partyIndex = BitConverter.ToUInt32(body, 8);
		if (num == 0 || num > 65535 || (operation != 0 && operation != 1) || ((operation == 0) ? (partyIndex > 10) : ((byte)partyIndex != 0)))
		{
			return false;
		}
		targetUserId = (ushort)num;
		return true;
	}

	private Task BroadcastRaidLeaderChangedAsync(RaidSnapshot changed)
	{
		return Task.WhenAll(from recipient in GetRaidChannelSessions(changed)
			select SessionDirectory.TrySendBestEffortAsync(async delegate(CancellationToken cancellationToken)
			{
				if (_raids.TryGetByRaidId(changed.RaidId, out var current) && !(current.InstanceId != changed.InstanceId))
				{
					if (_sessions.TryGet(checked((int)current.Leader.CharacterId), out var session) && session.SessionId == current.Leader.SessionId)
					{
						byte[] array = BuildRaidFormationUserContextPacket(session);
						if (array != null)
						{
							await recipient.SendPacketAsync(array, cancellationToken);
						}
					}
					await recipient.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidInfoUpdate(current.RaidId, current.TitleBytes, current.State, current.StateArgument, ToPacketMember(current.Leader))), cancellationToken);
					await recipient.SendPacketAsync(BuildRaidDirectoryPacket(ToDirectoryEntries(_raids.ListRaidsByChannel((int)(current.RaidId >> 16)))), cancellationToken);
				}
			}, $"raid leader changed raid={changed.RaidId} recipient={recipient.SessionId}"));
	}

	internal async Task SynchronizeNormalPartyJoinedAsync(ushort userId, Guid sessionId, Func<Party> readCurrentParty)
	{
		if (_raids.TrySynchronizeJoinedParty(userId, sessionId, readCurrentParty, out var raid))
		{
			await BroadcastRaidObjectAsync(raid);
			await BroadcastRaidMembersAsync(raid);
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_PARTY_REJOIN_SYNC raid={raid.RaidId} user={userId}");
		}
	}

	internal PartyOpResult LeaveNormalParty(ushort userId, Guid sessionId, Func<PartyOpResult> leave, out RaidSnapshot raid)
	{
		return _raids.LeaveNormalParty(userId, sessionId, leave, out raid);
	}

	internal Task PublishNormalPartyLeftAsync(RaidSnapshot changed, ushort userId)
	{
		if (changed == null)
		{
			return Task.CompletedTask;
		}
		FileLogger.Log($"[GameProtocol] RAID_PARTY_UNASSIGN raid={changed.RaidId} user={userId} partyIndex=0");
		return Task.WhenAll(from recipient in GetRaidChannelSessions(changed)
			select SessionDirectory.TrySendBestEffortAsync(async delegate(CancellationToken cancellationToken)
			{
				if (_raids.TryGetByRaidId(changed.RaidId, out var current) && !(current.InstanceId != changed.InstanceId))
				{
					await recipient.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MODIFY, RaidPacketBuilder.BuildRaidInfoUpdate(current.RaidId, current.TitleBytes, current.State, current.StateArgument, ToPacketMember(current.Leader))), cancellationToken);
					await recipient.SendPacketAsync(BuildRaidMembersPacketForRecipient(recipient, current.RaidId, ToPacketMembers(current)), cancellationToken);
					if (current.Members.Any((RaidMember m) => m.SessionId == recipient.SessionId))
					{
						IReadOnlyList<RaidMonsterStatusEntry> readOnlyList = BuildAntonRaidMonsterStatus(current);
						if (readOnlyList.Count > 0)
						{
							await recipient.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MONSTER_HP, RaidPacketBuilder.BuildRaidMonsterHp(readOnlyList)), cancellationToken);
						}
					}
					await recipient.SendPacketAsync(BuildRaidDirectoryPacket(ToDirectoryEntries(_raids.ListRaidsByChannel((int)(current.RaidId >> 16)))), cancellationToken);
				}
			}, $"raid party leave raid={changed.RaidId} recipient={recipient.SessionId}"));
	}
}
