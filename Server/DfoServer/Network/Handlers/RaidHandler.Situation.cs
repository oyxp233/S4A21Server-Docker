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
		if (!TryReadRaidBuffRequest(body, out var buffType, out var partyIndex, out var targetMemberIds))
		{
			FileLogger.Log($"[GameProtocol] RAID_BUFF_REQUEST_INVALID raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		FileLogger.Log($"[GameProtocol] RAID_BUFF_REQUEST raid={raid.RaidId} user={userId} type={buffType} party={partyIndex} members={string.Join(",", targetMemberIds ?? Array.Empty<ushort>())} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
		if (raid.State != 2 || !TryResolveAntonBuffDefinitionIndex(buffType, out var definitionIndex))
		{
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		RaidMember raidMember = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
		RaidBuffDefinition definition = AntonRaidRewardProvider.GetRaidBuffDefinitions()[definitionIndex];
		RaidBuffEntry config = definition.Entries.FirstOrDefault();
		if (raidMember == null || config == null || config.CooldownSeconds <= 0 || config.DurationSeconds < 0)
		{
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		ushort targetPartyIndex = ushort.MaxValue;
		ushort targetUserId = raidMember.UserId;
		IReadOnlyList<RaidSituationGroup> situationGroups = Array.Empty<RaidSituationGroup>();
		if (!string.Equals(config.Target, "RAID", StringComparison.OrdinalIgnoreCase) && (!_raids.TryGetSituationGroups(raid.RaidId, out situationGroups) || !TryResolveRequestedBuffTarget(raid, situationGroups, partyIndex, targetMemberIds, raidMember, out targetPartyIndex, out targetUserId)))
		{
			await SendAckAsync(session, header.type, success: false);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			return;
		}
		uint currentUnixTimestamp = GetCurrentUnixTimestamp();
		(uint, byte) key = (raid.RaidId, buffType);
		bool activated = false;
		RaidSnapshot extendedTimeRaid = null;
		RaidSnapshot extendedCoinRaid = null;
		uint extendedRemainingSeconds = 0u;
		bool flag = string.Equals(definition.TypeName, "INCREASE TIME", StringComparison.OrdinalIgnoreCase) && config.EffectValue > 0;
		bool flag2 = string.Equals(definition.TypeName, "INCREASE COIN", StringComparison.OrdinalIgnoreCase) && config.EffectValue > 0;
		checked
		{
			lock (_raidRuntimeLocks.GetOrAdd(raid.RaidId, (uint _) => new object()))
			{
				if (!_raidBuffActivations.TryGetValue(key, out var value) || value.CooldownUntilTimestamp <= currentUnixTimestamp)
				{
					bool flag3 = !flag || _raids.TryExtendPhaseTime(raid.RaidId, 2400u, (uint)config.EffectValue, out extendedTimeRaid, out extendedRemainingSeconds);
					if (flag3 & flag2)
					{
						flag3 = _raids.TryGrantAdditionalCoinUses(targetUserId, (uint)config.EffectValue, out extendedCoinRaid);
					}
					if (flag3)
					{
						_raidBuffActivations[key] = new AntonRaidBuffActivation
						{
							BuffType = buffType,
							PartyIndex = targetPartyIndex,
							UserId = targetUserId,
							ActiveUntilTimestamp = currentUnixTimestamp + (uint)config.DurationSeconds,
							CooldownUntilTimestamp = currentUnixTimestamp + (uint)config.CooldownSeconds
						};
						activated = true;
					}
				}
			}
			if (_raids.TryGetByUser(userId, out var raid2))
			{
				await BroadcastRaidBuffStatusAsync(raid2);
			}
			if (activated)
			{
				FileLogger.Log($"[GameProtocol] RAID_BUFF_ACTIVATE raid={raid.RaidId} user={userId} type={buffType} name={definition.TypeName} target={config.Target} party={targetPartyIndex} userTarget={targetUserId} duration={config.DurationSeconds} cooldown={config.CooldownSeconds} effect={config.EffectValue}");
				if (extendedTimeRaid != null)
				{
					await BroadcastRaidNotificationAsync(extendedTimeRaid, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, extendedRemainingSeconds));
					await BroadcastRaidNotificationAsync(extendedTimeRaid, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, extendedRemainingSeconds));
					StartAttackTimeoutTimer(extendedTimeRaid, extendedRemainingSeconds);
					FileLogger.Log($"[GameProtocol] RAID_BUFF_INCREASE_TIME raid={raid.RaidId} seconds={config.EffectValue} remaining={extendedRemainingSeconds}");
				}
				if (extendedCoinRaid != null)
				{
					await BroadcastRaidMonsterStatusAsync(extendedCoinRaid);
					FileLogger.Log($"[GameProtocol] RAID_BUFF_INCREASE_COIN raid={raid.RaidId} count={config.EffectValue} party={targetPartyIndex}");
				}
			}
			await SendAckAsync(session, header.type, activated);
		}
	}

	public async Task HandleRaidMonsterHp(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid))
		{
			return;
		}
		IReadOnlyList<RaidSituationGroup> situationGroups;
		if (!TryReadRaidMonsterRuntimeValues(body, out var values))
		{
			FileLogger.Log($"[GameProtocol] RAID_MONSTER_HP invalid raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
		}
		else if (values.Length == 0)
		{
			await EnsureRaidDungeonParticipationAsync(session, raid, userId);
			if (_raids.TryGetByUser(userId, out var raid2))
			{
				raid = raid2;
			}
			if (!_objectSent.ContainsKey(session.SessionId))
			{
				await SendRaidObjectAsync(session, raid);
			}
			_objectSent[session.SessionId] = 0;
			await SendRaidParticipationStatusAsync(session, raid, userId);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			await SendRaidMonsterStatusAsync(session, raid);
		}
		else if (_raids.TryGetSituationGroups(raid.RaidId, out situationGroups))
		{
			RaidSituationGroup raidSituationGroup = situationGroups.FirstOrDefault((RaidSituationGroup candidate) => candidate.MemberKeys.Contains(userId));
			if (raidSituationGroup != null && raidSituationGroup.DungeonId != 0)
			{
				_raidMonsterRuntimeValues[GetRaidMonsterRuntimeKey(raid.RaidId, raidSituationGroup)] = values;
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
		byte b = body[0];
		if (b > 5 || body.Length != 1 + b * 4)
		{
			return false;
		}
		values = new uint[b];
		for (int i = 0; i < values.Length; i++)
		{
			values[i] = BitConverter.ToUInt32(body, 1 + i * 4);
		}
		return true;
	}

	private static readonly byte[] AntonClientBuffTypes = new byte[5] { 2, 3, 4, 1, 0 };

	internal static bool TryResolveAntonBuffDefinitionIndex(byte clientBuffType, out int definitionIndex)
	{
		definitionIndex = clientBuffType switch
		{
			1 => 0,
			2 => 1,
			3 => 2,
			0 => 3,
			4 => 4,
			_ => -1,
		};
		return definitionIndex >= 0;
	}

	internal static IReadOnlyList<RaidBuffStatusGroup> BuildAntonRaidBuffStatus()
	{
		return AntonClientBuffTypes.Select((byte clientBuffType) => new RaidBuffStatusGroup
		{
			BuffType = clientBuffType,
			Entries = Array.Empty<RaidBuffStatusEntry>()
		}).ToArray();
	}

	private IReadOnlyList<RaidBuffStatusGroup> BuildAntonRaidBuffStatus(uint raidId)
	{
		uint currentUnixTimestamp = GetCurrentUnixTimestamp();
		Dictionary<byte, AntonRaidBuffActivation> activeByType = new Dictionary<byte, AntonRaidBuffActivation>();
		foreach (KeyValuePair<(uint, byte), AntonRaidBuffActivation> raidBuffActivation in _raidBuffActivations)
		{
			if (raidBuffActivation.Key.Item1 == raidId)
			{
				if (raidBuffActivation.Value.CooldownUntilTimestamp <= currentUnixTimestamp)
				{
					_raidBuffActivations.TryRemove(raidBuffActivation.Key, out var _);
				}
				else
				{
					activeByType[raidBuffActivation.Key.Item2] = raidBuffActivation.Value;
				}
			}
		}
		_raids.TryGetByRaidId(raidId, out var _);
		return AntonClientBuffTypes.Select(delegate(byte clientBuffType, int index)
		{
			byte b = clientBuffType;
			RaidBuffStatusGroup raidBuffStatusGroup = new RaidBuffStatusGroup
			{
				BuffType = b
			};
			IReadOnlyList<RaidBuffStatusEntry> readOnlyList = ((!activeByType.TryGetValue(b, out var value2)) ? ((IReadOnlyList<RaidBuffStatusEntry>)Array.Empty<RaidBuffStatusEntry>()) : ((IReadOnlyList<RaidBuffStatusEntry>)new RaidBuffStatusEntry[1]
			{
				new RaidBuffStatusEntry
				{
					PartyIndex = value2.PartyIndex,
					UserId = value2.UserId,
					ActiveUntilTimestamp = value2.ActiveUntilTimestamp,
					CooldownUntilTimestamp = value2.CooldownUntilTimestamp
				}
			}));
			IReadOnlyList<RaidBuffStatusEntry> entries = readOnlyList;
			raidBuffStatusGroup.Entries = entries;
			return raidBuffStatusGroup;
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
		List<ushort> list = new List<ushort>((body.Length - 3) / 2);
		for (int i = 3; i + 2 <= body.Length; i += 2)
		{
			list.Add(BitConverter.ToUInt16(body, i));
		}
		targetMemberIds = list;
		return true;
	}

	internal static bool TryResolveRequestedBuffTarget(RaidSnapshot raid, IReadOnlyList<RaidSituationGroup> situationGroups, ushort requestedPartyIndex, IReadOnlyList<ushort> targetMemberIds, RaidMember activatingMember, out ushort partyIndex, out ushort targetUserId)
	{
		partyIndex = ushort.MaxValue;
		targetUserId = 0;
		if (raid == null || activatingMember == null || situationGroups == null)
		{
			return false;
		}
		RaidSituationGroup group = situationGroups.FirstOrDefault((RaidSituationGroup candidate) => candidate.PartyIndex == requestedPartyIndex);
		if (group == null)
		{
			group = situationGroups.FirstOrDefault((RaidSituationGroup candidate) => candidate.SituationIndex == requestedPartyIndex);
		}
		if (group == null || group.DungeonId == 0 || group.MemberKeys.Count == 0)
		{
			return false;
		}
		IReadOnlyList<ushort> readOnlyList = targetMemberIds ?? Array.Empty<ushort>();
		if (readOnlyList.Count > 0 && readOnlyList.Distinct().Any((ushort memberId) => !group.MemberKeys.Contains(memberId)))
		{
			return false;
		}
		ushort preferredTargetUserId = ((readOnlyList.Count > 0) ? readOnlyList[0] : checked((ushort)group.MemberKeys[0]));
		RaidMember raidMember = raid.Members.FirstOrDefault((RaidMember candidate) => candidate.UserId == preferredTargetUserId && group.MemberKeys.Contains(candidate.UserId));
		if (raidMember == null)
		{
			return false;
		}
		partyIndex = group.PartyIndex;
		targetUserId = raidMember.UserId;
		return true;
	}

	private static uint GetCurrentUnixTimestamp()
	{
		return checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
	}

	private Task SendRaidBuffStatusAsync(EnhancedClientSession session, uint raidId)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_BUFF_SYSTEM, RaidPacketBuilder.BuildRaidBuffSystem(BuildAntonRaidBuffStatus(raidId))));
	}

	private Task BroadcastRaidBuffStatusAsync(RaidSnapshot raid)
	{
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_BUFF_SYSTEM, RaidPacketBuilder.BuildRaidBuffSystem(BuildAntonRaidBuffStatus(raid.RaidId)));
	}

	private IReadOnlyList<RaidMonsterStatusEntry> BuildAntonRaidMonsterStatus(RaidSnapshot raid)
	{
		if (raid == null || !_raids.TryGetSituationGroups(raid.RaidId, out var situationGroups))
		{
			return Array.Empty<RaidMonsterStatusEntry>();
		}
		List<RaidMonsterStatusEntry> list = new List<RaidMonsterStatusEntry>(situationGroups.Count);
		foreach (RaidSituationGroup item in situationGroups)
		{
			_raidMonsterRuntimeValues.TryGetValue(GetRaidMonsterRuntimeKey(raid.RaidId, item, item.DungeonId), out var value);
			RaidMonsterStatusEntry raidMonsterStatusEntry = new RaidMonsterStatusEntry
			{
				SituationIndex = item.PartyIndex,
				MemberIds = item.MemberKeys.Select((uint id) => checked((ushort)id)).ToArray(),
				UsedCoinCount = GetAntonReportedUsedCoinCount(item.UsedCoinCount, item.GrantedCoinCount)
			};
			IReadOnlyList<uint> readOnlyList2;
			if (value != null)
			{
				IReadOnlyList<uint> readOnlyList = (uint[])value.Clone();
				readOnlyList2 = readOnlyList;
			}
			else
			{
				IReadOnlyList<uint> readOnlyList = Array.Empty<uint>();
				readOnlyList2 = readOnlyList;
			}
			IReadOnlyList<uint> runtimeValues = readOnlyList2;
			raidMonsterStatusEntry.RuntimeValues = runtimeValues;
			list.Add(raidMonsterStatusEntry);
		}
		return list;
	}

	internal static uint GetAntonReportedUsedCoinCount(uint usedCoinCount, uint grantedCoinCount)
	{
		if (usedCoinCount <= grantedCoinCount)
		{
			return 0u;
		}
		return usedCoinCount - grantedCoinCount;
	}

	private void ResetRaidMonsterRuntimeValues(RaidSnapshot raid, ushort userId, uint dungeonId)
	{
		if (raid != null && _raids.TryGetSituationGroups(raid.RaidId, out var situationGroups))
		{
			RaidSituationGroup raidSituationGroup = situationGroups.FirstOrDefault((RaidSituationGroup candidate) => candidate.MemberKeys.Contains(userId));
			if (raidSituationGroup != null)
			{
				_raidMonsterRuntimeValues.TryRemove(GetRaidMonsterRuntimeKey(raid.RaidId, raidSituationGroup, dungeonId), out var _);
			}
		}
	}

	private static (uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId)
		GetRaidMonsterRuntimeKey(uint raidId, RaidSituationGroup group)
	{
		return GetRaidMonsterRuntimeKey(raidId, group, group.DungeonId);
	}

	private static (uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId) GetRaidMonsterRuntimeKey(uint raidId, RaidSituationGroup group, uint dungeonId)
	{
		uint item = ((group.IsSolo && group.MemberKeys.Count > 0) ? group.MemberKeys[0] : 0u);
		return (RaidId: raidId, SituationIndex: group.SituationIndex, SoloMemberKey: item, DungeonId: dungeonId);
	}
	private Task SendRaidMonsterStatusAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		IReadOnlyList<RaidMonsterStatusEntry> readOnlyList = BuildAntonRaidMonsterStatus(raid);
		if (readOnlyList.Count == 0)
		{
			return Task.CompletedTask;
		}
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_MONSTER_HP, RaidPacketBuilder.BuildRaidMonsterHp(readOnlyList)));
	}


	private async Task EnsureRaidDungeonParticipationAsync(EnhancedClientSession session, RaidSnapshot raid, ushort userId)
	{
		DungeonRun run = session?.Player?.CurrentRun;
		if (run != null && IsAntonRaidDungeon(run.DungeonId) && IsAntonDungeonForPhase(raid.PhaseIndex, run.DungeonId) && _raids.TryEnterDungeon(userId, (uint)run.DungeonId, out var enteredRaid, out var memberKeys))
		{
			ResetRaidMonsterRuntimeValues(enteredRaid, userId, (uint)run.DungeonId);
			await BroadcastRaidParticipationEnterAsync(enteredRaid, (uint)run.DungeonId, memberKeys);
			await BroadcastRaidMonsterStatusAsync(enteredRaid);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ENTER_LATE raid={enteredRaid.RaidId} phase={enteredRaid.PhaseIndex} dungeon={run.DungeonId} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	private async Task BroadcastRaidParticipationEnterAsync(RaidSnapshot raid, uint dungeonId, IReadOnlyList<uint> memberKeys)
	{
		await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo(dungeonId, 0u, memberKeys));
		await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo(dungeonId, 1u, memberKeys));
	}

	private async Task SendRaidParticipationStatusAsync(EnhancedClientSession session, RaidSnapshot raid, ushort userId)
	{
		if (raid == null || !_raids.TryGetSituationGroups(raid.RaidId, out var situationGroups))
		{
			return;
		}
		foreach (byte[] item in BuildRaidParticipationRefreshPackets(situationGroups))
		{
			await session.SendPacketAsync(item);
		}
	}
	private Task BroadcastRaidMonsterStatusAsync(RaidSnapshot raid)
	{
		IReadOnlyList<RaidMonsterStatusEntry> readOnlyList = BuildAntonRaidMonsterStatus(raid);
		if (readOnlyList.Count == 0)
		{
			return Task.CompletedTask;
		}
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_MONSTER_HP, RaidPacketBuilder.BuildRaidMonsterHp(readOnlyList));
	}

	private async Task BroadcastRaidSituationAsync(RaidSnapshot raid)
	{
		await BroadcastRaidBuffStatusAsync(raid);
		await BroadcastRaidMonsterStatusAsync(raid);
	}

	internal static IReadOnlyList<byte[]> BuildRaidParticipationRefreshPackets(IReadOnlyList<RaidSituationGroup> groups)
	{
		if (groups == null)
		{
			throw new ArgumentNullException("groups");
		}
		List<byte[]> list = new List<byte[]>();
		foreach (RaidSituationGroup group in groups)
		{
			if (group.DungeonId != 0 && group.MemberKeys.Count != 0)
			{
				uint[] array = (group.DungeonCleared ? new uint[3] { 0u, 2u, 4u } : new uint[2] { 0u, 2u });
				foreach (uint op in array)
				{
					list.Add(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo(group.DungeonId, op, group.MemberKeys)));
				}
			}
		}
		return list;
	}
}
