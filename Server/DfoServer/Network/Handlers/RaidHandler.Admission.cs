using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{

	private sealed class RaidEntryCostLease
	{
		public EnhancedClientSession Session { get; }

		public InventoryLease Lease { get; }

		public RaidEntryCostLease(EnhancedClientSession session, InventoryLease lease)
		{
			Session = session;
			Lease = lease;
		}
	}

	private sealed class RaidConsumedEntryCost
	{
		public EnhancedClientSession Session { get; }

		public short SlotIndex { get; }

		public RaidConsumedEntryCost(EnhancedClientSession session, short slotIndex)
		{
			Session = session;

			SlotIndex = slotIndex;
		}
	}

	public async Task HandleEntryCostInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		if (!_raids.TryGetByUser(userId, out var raid))
		{
			// Opening the raid status window is a read-only query. Do not create
			// a raid here: the client can send this request from town before the
			// explicit CREATE_RAID flow, which otherwise leaves a phantom raid.
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		byte stage = (byte)((body != null && body.Length != 0) ? body[0] : 0);
		await EnsureRaidDungeonParticipationAsync(session, raid, userId);
		if (_raids.TryGetByUser(userId, out var refreshedRaid))
			raid = refreshedRaid;
		// Member cache refresh is limited to party edits and START_RAID_ATTACK.
		// Opening the status window is read-only; replaying operation=3 here can
		// race the client state handler and recreate the raid-start banner.
		if (!_objectSent.ContainsKey(session.SessionId))
		{
			await SendRaidObjectAsync(session, raid);
			_objectSent[session.SessionId] = 0;
		}
		await SendAckAsync(session, header.type, success: true);
		if (stage == 0)
		{
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			await SendRaidMonsterStatusAsync(session, raid);
		}
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 599, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(raid))));
	}


	private static IReadOnlyList<RaidEntryCostStatus> BuildEntryCostStatuses(RaidSnapshot raid)
	{
		List<RaidEntryCostStatus> result = new List<RaidEntryCostStatus>(raid.Members.Count);
		checked
		{
			foreach (RaidMember member in raid.Members)
			{
				int ownedCount = 0;
				if (InventoryContext.TryGetLease((int)member.CharacterId, out var lease) && lease.IsOwnedBy(member.SessionId))
				{
					lock (lease.SyncRoot)
					{
						ownedCount = Math.Max(0, lease.Inventory.CountMainItem(10096296));
					}
				}
				result.Add(new RaidEntryCostStatus
				{
					UserId = member.UserId,
					Ready = (raid.State != 0 || ownedCount >= 1),
					OwnedCount = (uint)ownedCount
				});
			}
			return result;
		}
	}

	private static bool HasAllEntryCosts(RaidSnapshot raid)
	{
		if (raid == null || raid.State != 0 || raid.Members.Count == 0)
		{
			return false;
		}
		foreach (RaidMember member in raid.Members)
		{
			if (!InventoryContext.TryGetLease(checked((int)member.CharacterId), out var lease) || !lease.IsOwnedBy(member.SessionId))
			{
				return false;
			}
			lock (lease.SyncRoot)
			{
				if (lease.Inventory.CountMainItem(10096296) < 1)
				{
					return false;
				}
			}
		}
		return true;
	}

	private bool TryConsumeEntryCosts(RaidSnapshot raid, out List<RaidConsumedEntryCost> consumedCosts)
	{
		consumedCosts = new List<RaidConsumedEntryCost>();
		List<RaidEntryCostLease> leases = new List<RaidEntryCostLease>(raid.Members.Count);
		foreach (RaidMember member in raid.Members.OrderBy((RaidMember raidMember) => raidMember.CharacterId))
		{
			int characterId = checked((int)member.CharacterId);
			if (!_sessions.TryGet(characterId, out var memberSession) || memberSession.SessionId != member.SessionId || !InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(member.SessionId))
			{
				return false;
			}
			leases.Add(new RaidEntryCostLease(memberSession, lease));
		}

		if (!RaidEntryCostCommitService.TryConsume(
				leases.Select(entry => entry.Lease).ToArray(),
				out var mutations))
		{
			return false;
		}

		var sessionsByCharacterId = leases.ToDictionary(
			entry => entry.Lease.CharacterId,
			entry => entry.Session);
		foreach (var mutation in mutations)
		{
			if (!sessionsByCharacterId.TryGetValue(
					mutation.CharacterId,
					out var memberSession))
				return false;

			consumedCosts.Add(new RaidConsumedEntryCost(
				memberSession,
				mutation.SlotIndex));
		}

		return true;
	}

}
