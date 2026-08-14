using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Raid;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;
using PvfLib;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	private sealed class AntonRaidBuffActivation
	{
		public byte BuffType { get; init; }

		public ushort PartyIndex { get; init; }

		public ushort UserId { get; init; }

		public uint ActiveUntilTimestamp { get; init; }

		public uint CooldownUntilTimestamp { get; init; }
	}

	public async Task HandleRaidBuffSystem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		if (!TryReadRaidBuffRequest(body, out var buffType, out var requestedPartyIndex, out var targetMemberIds))
		{
			FileLogger.Log($"[GameProtocol] RAID_BUFF_REQUEST_INVALID raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		FileLogger.Log($"[GameProtocol] RAID_BUFF_REQUEST raid={raid.RaidId} user={userId} type={buffType} party={requestedPartyIndex} members={string.Join(",", targetMemberIds ?? Array.Empty<ushort>())} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
		if (raid.State != 2 || !TryResolveAntonBuffDefinitionIndex(buffType, out var definitionIndex))
		{
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		RaidMember member = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
		RaidBuffDefinition definition = AntonRaidRewardProvider.GetRaidBuffDefinitions()[definitionIndex];
		RaidBuffEntry config = definition.Entries.FirstOrDefault();
		if (member == null || config == null || config.CooldownSeconds <= 0 || config.DurationSeconds < 0)
		{
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		ushort targetPartyIndex = ushort.MaxValue;
		ushort targetUserId = member.UserId;
		IReadOnlyList<RaidSituationGroup> situationGroups = Array.Empty<RaidSituationGroup>();
		if (!string.Equals(config.Target, "RAID", StringComparison.OrdinalIgnoreCase)
			&& (!_raids.TryGetSituationGroups(raid.RaidId, out situationGroups)
				|| !TryResolveRequestedBuffTarget(raid, situationGroups, requestedPartyIndex, targetMemberIds, member, out targetPartyIndex, out targetUserId)))
		{
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		uint now = GetCurrentUnixTimestamp();
		(uint RaidId, byte buffType) key = (RaidId: raid.RaidId, buffType: buffType);
		bool activated = false;
		RaidSnapshot extendedTimeRaid = null;
		RaidSnapshot extendedCoinRaid = null;
		uint extendedRemainingSeconds = 0;
		bool increasesTime = string.Equals(definition.TypeName, "INCREASE TIME", StringComparison.OrdinalIgnoreCase)
			&& config.EffectValue > 0;
		bool increasesCoins = string.Equals(definition.TypeName, "INCREASE COIN", StringComparison.OrdinalIgnoreCase)
			&& config.EffectValue > 0;
		object syncRoot = _raidRuntimeLocks.GetOrAdd(raid.RaidId, (uint _) => new object());
		checked
		{
			lock (syncRoot)
			{
				if (!_raidBuffActivations.TryGetValue(key, out var current) || current.CooldownUntilTimestamp <= now)
				{
					// A full 40-minute timer has no room for this buff. Do not consume
					// its two-hour cooldown until it actually extends the phase timer.
					bool effectApplied = !increasesTime || _raids.TryExtendPhaseTime(
						raid.RaidId,
						AttackSeconds,
						(uint)config.EffectValue,
						out extendedTimeRaid,
						out extendedRemainingSeconds);
					if (effectApplied && increasesCoins)
						effectApplied = _raids.TryGrantAdditionalCoinUses(
							targetUserId,
							(uint)config.EffectValue,
							out extendedCoinRaid);
					if (effectApplied)
					{
						_raidBuffActivations[key] = new AntonRaidBuffActivation
						{
							BuffType = buffType,
							PartyIndex = targetPartyIndex,
							UserId = targetUserId,
							ActiveUntilTimestamp = now + (uint)config.DurationSeconds,
							CooldownUntilTimestamp = now + (uint)config.CooldownSeconds
						};
						activated = true;
					}
				}
			}
			if (_raids.TryGetByUser(userId, out var currentRaid))
			{
				await BroadcastRaidBuffStatusAsync(currentRaid);
			}
			if (activated)
			{
				FileLogger.Log($"[GameProtocol] RAID_BUFF_ACTIVATE raid={raid.RaidId} user={userId} type={buffType} name={definition.TypeName} target={config.Target} party={targetPartyIndex} userTarget={targetUserId} duration={config.DurationSeconds} cooldown={config.CooldownSeconds} effect={config.EffectValue}");
				if (extendedTimeRaid != null)
				{
					await BroadcastRaidNotificationAsync(extendedTimeRaid, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, extendedRemainingSeconds));
					await BroadcastRaidNotificationAsync(extendedTimeRaid, NotiPacketType.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, extendedRemainingSeconds));
					StartAttackTimeoutTimer(extendedTimeRaid, extendedRemainingSeconds);
					FileLogger.Log($"[GameProtocol] RAID_BUFF_INCREASE_TIME raid={raid.RaidId} seconds={config.EffectValue} remaining={extendedRemainingSeconds}");
				}
				if (extendedCoinRaid != null)
				{
					await BroadcastRaidMonsterStatusAsync(extendedCoinRaid);
					FileLogger.Log($"[GameProtocol] RAID_BUFF_INCREASE_COIN raid={raid.RaidId} count={config.EffectValue} party={targetPartyIndex}");
				}
			}
		}
		await SendAckAsync(session, header.type, activated);
	}

	public async Task HandleRaidMonsterHp(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid))
		{
			return;
		}

		IReadOnlyList<RaidSituationGroup> groups;
		if (!TryReadRaidMonsterRuntimeValues(body, out var values))
		{
			FileLogger.Log($"[GameProtocol] RAID_MONSTER_HP invalid raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
		}
		else if (values.Length == 0)
		{
			await EnsureRaidDungeonParticipationAsync(session, raid, userId);
			if (_raids.TryGetByUser(userId, out var refreshedRaid))
				raid = refreshedRaid;
			// The client sends 0x2B8 as u8(0) when the situation window opens.
			// This is read-only; member cache refresh happens at start or edits.
			if (!_objectSent.ContainsKey(session.SessionId))
				await SendRaidObjectAsync(session, raid);
			_objectSent[session.SessionId] = 0;
			await SendRaidParticipationStatusAsync(session, raid, userId);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			await SendRaidMonsterStatusAsync(session, raid);
		}
		else if (_raids.TryGetSituationGroups(raid.RaidId, out groups))
		{
			RaidSituationGroup group = groups.FirstOrDefault((RaidSituationGroup candidate) => candidate.MemberKeys.Contains(userId));
			if (group != null && group.DungeonId != 0)
			{
				_raidMonsterRuntimeValues[GetRaidMonsterRuntimeKey(raid.RaidId, group)] = values;
				await BroadcastRaidMonsterStatusAsync(raid);
			}
		}
	}

	internal static bool TryReadRaidMonsterRuntimeValues(byte[] body, out uint[] values)
	{
		values = Array.Empty<uint>();
		if (body == null || body.Length < 1)
		{
			return false;
		}
		byte valueCount = body[0];
		if (valueCount > 5 || body.Length != 1 + valueCount * 4)
		{
			return false;
		}
		values = new uint[valueCount];
		for (int index = 0; index < values.Length; index++)
		{
			values[index] = BitConverter.ToUInt32(body, 1 + index * 4);
		}
		return true;
	}

	private static readonly byte[] AntonClientBuffTypes = new byte[] { 2, 3, 4, 1, 0 };

	internal static bool TryResolveAntonBuffDefinitionIndex(byte clientBuffType, out int definitionIndex)
	{
		definitionIndex = clientBuffType switch
		{
			1 => 0, // ATTACK BONUS (client attack button)
			2 => 1, // INVINCIBLE
			3 => 2, // RESTORE
			0 => 3, // INCREASE TIME
			4 => 4, // INCREASE COIN
			_ => -1
		};
		return definitionIndex >= 0;
	}

	internal static IReadOnlyList<RaidBuffStatusGroup> BuildAntonRaidBuffStatus()
	{
		return AntonClientBuffTypes.Select(clientBuffType => new RaidBuffStatusGroup
		{
			BuffType = clientBuffType,
			Entries = Array.Empty<RaidBuffStatusEntry>()
		}).ToArray();
	}

	private IReadOnlyList<RaidBuffStatusGroup> BuildAntonRaidBuffStatus(uint raidId)
	{
		uint now = GetCurrentUnixTimestamp();
		Dictionary<byte, AntonRaidBuffActivation> activeByType = new Dictionary<byte, AntonRaidBuffActivation>();
		foreach (KeyValuePair<(uint, byte), AntonRaidBuffActivation> pair in _raidBuffActivations)
		{
			if (pair.Key.Item1 == raidId)
			{
				if (pair.Value.CooldownUntilTimestamp <= now)
				{
					_raidBuffActivations.TryRemove(pair.Key, out var _);
				}
				else
				{
					activeByType[pair.Key.Item2] = pair.Value;
				}
			}
		}
		_raids.TryGetByRaidId(raidId, out var raid);
		return AntonClientBuffTypes.Select(delegate(byte clientBuffType, int index)
		{
			byte b = clientBuffType;
			AntonRaidBuffActivation value2;
			return new RaidBuffStatusGroup
			{
				BuffType = b,
				Entries = ((!activeByType.TryGetValue(b, out value2)) ? ((IReadOnlyList<RaidBuffStatusEntry>)Array.Empty<RaidBuffStatusEntry>()) : ((IReadOnlyList<RaidBuffStatusEntry>)new RaidBuffStatusEntry[1]
				{
					new RaidBuffStatusEntry
					{
						PartyIndex = value2.PartyIndex,
						UserId = value2.UserId,
						ActiveUntilTimestamp = value2.ActiveUntilTimestamp,
						CooldownUntilTimestamp = value2.CooldownUntilTimestamp
					}
				}))
			};
		}).ToArray();
	}

	internal static bool TryReadRaidBuffRequest(byte[] body, out byte buffType, out ushort partyIndex, out IReadOnlyList<ushort> targetMemberIds)
	{
		buffType = 0;
		partyIndex = ushort.MaxValue;
		targetMemberIds = Array.Empty<ushort>();
		if (body == null || body.Length == 0)
		{
			return false;
		}
		buffType = body[0];
		if (body.Length < 3)
		{
			return true;
		}
		partyIndex = BitConverter.ToUInt16(body, 1);
		List<ushort> targets = new List<ushort>((body.Length - 3) / 2);
		for (int offset = 3; offset + 2 <= body.Length; offset += 2)
		{
			targets.Add(BitConverter.ToUInt16(body, offset));
		}
		targetMemberIds = targets;
		return true;
	}

	internal static bool TryResolveRequestedBuffTarget(
		RaidSnapshot raid,
		IReadOnlyList<RaidSituationGroup> situationGroups,
		ushort requestedPartyIndex,
		IReadOnlyList<ushort> targetMemberIds,
		RaidMember activatingMember,
		out ushort partyIndex,
		out ushort targetUserId)
	{
		partyIndex = ushort.MaxValue;
		targetUserId = 0;
		if (raid == null || activatingMember == null || situationGroups == null)
		{
			return false;
		}

		// IDA sub_6E17D0 serializes the selected row's first scalar followed by
		// every u16 member id from that row. The scalar is the displayed party
		// number; older server builds sent the zero-based situation row instead.
		RaidSituationGroup group = situationGroups.FirstOrDefault(candidate =>
			candidate.PartyIndex == requestedPartyIndex);
		if (group == null)
		{
			// Accept rows cached from an older status packet during a rolling update.
			group = situationGroups.FirstOrDefault(candidate =>
				candidate.SituationIndex == requestedPartyIndex);
		}
		if (group == null || group.DungeonId == 0 || group.MemberKeys.Count == 0)
		{
			return false;
		}

		IReadOnlyList<ushort> requestedMembers = targetMemberIds ?? Array.Empty<ushort>();
		if (requestedMembers.Count > 0
			&& requestedMembers.Distinct().Any(memberId => !group.MemberKeys.Contains(memberId)))
		{
			return false;
		}

		ushort preferredTargetUserId = requestedMembers.Count > 0
			? requestedMembers[0]
			: checked((ushort)group.MemberKeys[0]);
		RaidMember targetMember = raid.Members.FirstOrDefault(candidate =>
			candidate.UserId == preferredTargetUserId
			&& group.MemberKeys.Contains(candidate.UserId));
		if (targetMember == null)
		{
			return false;
		}

		partyIndex = group.PartyIndex;
		targetUserId = targetMember.UserId;
		return true;
	}

	private static uint GetCurrentUnixTimestamp()
	{
		return checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
	}

	private Task SendRaidBuffStatusAsync(EnhancedClientSession session, uint raidId)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 634, RaidPacketBuilder.BuildRaidBuffSystem(BuildAntonRaidBuffStatus(raidId))));
	}

	private Task BroadcastRaidBuffStatusAsync(RaidSnapshot raid)
	{
		return BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_BUFF_SYSTEM, RaidPacketBuilder.BuildRaidBuffSystem(BuildAntonRaidBuffStatus(raid.RaidId)));
	}

	private IReadOnlyList<RaidMonsterStatusEntry> BuildAntonRaidMonsterStatus(RaidSnapshot raid)
	{
		if (raid == null || !_raids.TryGetSituationGroups(raid.RaidId, out var groups))
			return Array.Empty<RaidMonsterStatusEntry>();

		var result = new List<RaidMonsterStatusEntry>(groups.Count);
		foreach (var group in groups)
		{
			_raidMonsterRuntimeValues.TryGetValue(
				GetRaidMonsterRuntimeKey(raid.RaidId, group, group.DungeonId),
				out var runtimeValues);

			result.Add(new RaidMonsterStatusEntry
			{
				SituationIndex = group.PartyIndex,
				MemberIds = group.MemberKeys.Select(id => checked((ushort)id)).ToArray(),
				UsedCoinCount = GetAntonReportedUsedCoinCount(group.UsedCoinCount, group.GrantedCoinCount),
				RuntimeValues = runtimeValues == null
					? Array.Empty<uint>()
					: (uint[])runtimeValues.Clone(),
			});
		}
		return result;
	}

	internal static uint GetAntonReportedUsedCoinCount(uint usedCoinCount, uint grantedCoinCount)
	{
		return usedCoinCount > grantedCoinCount
			? usedCoinCount - grantedCoinCount
			: 0u;
	}

	private void ResetRaidMonsterRuntimeValues(RaidSnapshot raid, ushort userId, uint dungeonId)
	{
		if (raid == null || !_raids.TryGetSituationGroups(raid.RaidId, out var groups))
			return;

		var group = groups.FirstOrDefault(candidate => candidate.MemberKeys.Contains(userId));
		if (group == null)
			return;

		_raidMonsterRuntimeValues.TryRemove(
			GetRaidMonsterRuntimeKey(raid.RaidId, group, dungeonId),
			out _);
	}

	private static (uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId)
		GetRaidMonsterRuntimeKey(uint raidId, RaidSituationGroup group)
	{
		return GetRaidMonsterRuntimeKey(raidId, group, group.DungeonId);
	}

	private static (uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId)
		GetRaidMonsterRuntimeKey(uint raidId, RaidSituationGroup group, uint dungeonId)
	{
		var soloMemberKey = group.IsSolo && group.MemberKeys.Count > 0
			? group.MemberKeys[0]
			: 0u;
		return (raidId, group.SituationIndex, soloMemberKey, dungeonId);
	}
	private Task SendRaidMonsterStatusAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		IReadOnlyList<RaidMonsterStatusEntry> statuses = BuildAntonRaidMonsterStatus(raid);
		if (statuses.Count == 0)
		{
			return Task.CompletedTask;
		}
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 635, RaidPacketBuilder.BuildRaidMonsterHp(statuses)));
	}


	private async Task EnsureRaidDungeonParticipationAsync(
		EnhancedClientSession session,
		RaidSnapshot raid,
		ushort userId)
	{
		DungeonRun run = session?.Player?.CurrentRun;
		if (run == null
			|| !IsAntonRaidDungeon(run.DungeonId)
			|| !IsAntonDungeonForPhase(raid.PhaseIndex, run.DungeonId))
			return;

		if (_raids.TryEnterDungeon(
			userId,
			(uint)run.DungeonId,
			out var enteredRaid,
			out var memberKeys))
		{
			ResetRaidMonsterRuntimeValues(enteredRaid, userId, (uint)run.DungeonId);
			await BroadcastRaidParticipationEnterAsync(
				enteredRaid,
				(uint)run.DungeonId,
				memberKeys);
			await BroadcastRaidMonsterStatusAsync(enteredRaid);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ENTER_LATE raid={enteredRaid.RaidId} phase={enteredRaid.PhaseIndex} dungeon={run.DungeonId} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	private async Task BroadcastRaidParticipationEnterAsync(
		RaidSnapshot raid,
		uint dungeonId,
		IReadOnlyList<uint> memberKeys)
	{
		// The client appends op=1 records and can miss the op=0 notification while
		// changing scenes. Remove a stale record immediately before every entry so
		// re-entering the same dungeon cannot create duplicate party rows.
		await BroadcastRaidNotificationAsync(
			raid,
			NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO,
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(dungeonId, 0u, memberKeys));
		await BroadcastRaidNotificationAsync(
			raid,
			NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO,
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(dungeonId, 1u, memberKeys));
	}

	private async Task SendRaidParticipationStatusAsync(EnhancedClientSession session, RaidSnapshot raid, ushort userId)
	{
		if (raid == null || !_raids.TryGetSituationGroups(raid.RaidId, out var groups))
			return;

		RaidSituationGroup group = groups.FirstOrDefault(candidate => candidate.MemberKeys.Contains(userId));
		if (group == null || group.DungeonId == 0 || group.MemberKeys.Count == 0)
			return;

		// Rebuild the client-side row on status-window refresh. This also repairs a
		// stale row when the exit notification was dropped during a scene change.
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
			0,
			585,
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(
				group.DungeonId,
				0u,
				group.MemberKeys)));
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
			0,
			585,
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(
				group.DungeonId,
				2u,
				group.MemberKeys)));
	}
	private Task BroadcastRaidMonsterStatusAsync(RaidSnapshot raid)
	{
		IReadOnlyList<RaidMonsterStatusEntry> statuses = BuildAntonRaidMonsterStatus(raid);
		if (statuses.Count == 0)
		{
			return Task.CompletedTask;
		}
		return BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_MONSTER_HP, RaidPacketBuilder.BuildRaidMonsterHp(statuses));
	}

	private async Task BroadcastRaidSituationAsync(RaidSnapshot raid)
	{
		await BroadcastRaidBuffStatusAsync(raid);
		await BroadcastRaidMonsterStatusAsync(raid);
	}
}
