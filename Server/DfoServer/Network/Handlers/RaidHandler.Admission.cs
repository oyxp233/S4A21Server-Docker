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
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		byte stage = (byte)((body != null && body.Length != 0) ? body[0] : 0);
		await EnsureRaidDungeonParticipationAsync(session, raid, userId);
		if (_raids.TryGetByUser(userId, out var raid2))
		{
			raid = raid2;
		}
		if (!_objectSent.ContainsKey(session.SessionId))
		{
			await SendRaidObjectAsync(session, raid);
			_objectSent[session.SessionId] = 0;
		}
		await SendAckAsync(session, header.type, success: true);
		if (stage == 0)
		{
			await SendRaidBuffStatusAsync(session, raid);
			await SendRaidMonsterStatusAsync(session, raid);
		}
		await RefreshEntryCostsAsync(session);
	}


	private static IReadOnlyList<RaidEntryCostStatus> BuildEntryCostStatuses(RaidSnapshot raid)
	{
		List<RaidEntryCostStatus> list = new List<RaidEntryCostStatus>(raid.Members.Count);
		checked
		{
			foreach (RaidMember member in raid.Members)
			{
				int num = 0;
				if (InventoryContext.TryGetLease((int)member.CharacterId, out var lease) && lease.IsOwnedBy(member.SessionId))
				{
					lock (lease.SyncRoot)
					{
						num = Math.Max(0, lease.Inventory.CountMainItem(10096296));
					}
				}
				list.Add(new RaidEntryCostStatus
				{
					UserId = member.UserId,
					Ready = (raid.State != 0 || num >= 1),
					OwnedCount = (uint)num
				});
			}
			return list;
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
		List<RaidEntryCostLease> list = new List<RaidEntryCostLease>(raid.Members.Count);
		foreach (RaidMember item in raid.Members.OrderBy((RaidMember raidMember) => raidMember.CharacterId))
		{
			int characterId = checked((int)item.CharacterId);
			if (!_sessions.TryGet(characterId, out var session) || session.SessionId != item.SessionId || !InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(item.SessionId))
			{
				return false;
			}
			list.Add(new RaidEntryCostLease(session, lease));
		}
		if (!RaidEntryCostCommitService.TryConsume(list.Select((RaidEntryCostLease entry) => entry.Lease).ToArray(), out var mutations))
		{
			return false;
		}
		Dictionary<int, EnhancedClientSession> dictionary = list.ToDictionary((RaidEntryCostLease entry) => entry.Lease.CharacterId, (RaidEntryCostLease entry) => entry.Session);
		foreach (RaidEntryCostMutation item2 in mutations)
		{
			if (!dictionary.TryGetValue(item2.CharacterId, out var value))
			{
				return false;
			}
			consumedCosts.Add(new RaidConsumedEntryCost(value, item2.SlotIndex));
		}
		return true;
	}

	public Task RefreshEntryCostsAsync(EnhancedClientSession session)
	{
		if (!IsRaidSession(session) || !TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid) || !raid.Members.Any((RaidMember member) => member.UserId == userId && member.SessionId == session.SessionId))
		{
			return Task.CompletedTask;
		}
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_ENTRY_COST_INFO, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(raid)));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

}
