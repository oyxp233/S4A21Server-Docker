using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	public async Task HandleStartRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		bool flag = body == null || body.Length == 0;
		if (flag && TryResolveUserId(session, out var userId) && _raids.TryGetByUser(userId, out var raid) && raid.State == 5 && raid.StateArgument == 0 && raid.PhaseIndex == 0)
		{
			await HandleStartNextRaidPhaseAsync(session, header, body);
			return;
		}
		if (flag && TryResolveUserId(session, out var checkUserId) && _raids.TryGetByUser(checkUserId, out var costCheckRaid) && !HasAllEntryCosts(costCheckRaid))
		{
			await SendAckAsync(session, header.type, success: false);
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice("攻坚队中存在未准备入场材料的队员。", 0)));
			FileLogger.Log($"[GameProtocol] START_RAID rejected entry-cost raid={costCheckRaid.RaidId} user={checkUserId}");
			return;
		}
		if (!flag || !TryResolveUserId(session, out var userId2) || !_raids.TryGetByUser(userId2, out var raid2) || !HasAllEntryCosts(raid2) || !_raids.TryBeginStart(userId2, out var raid3))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		await SendAckAsync(session, header.type, success: true);
		FileLogger.Log($"[GameProtocol] START_RAID_READY raid={raid3.RaidId} leader={userId2}");
		await BroadcastRaidObjectAsync(raid3);
		uint readySeconds = AntonRaidRewardProvider.GetStartDelaySeconds();
		await BroadcastRaidNotificationAsync(raid3, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, readySeconds));
		await BroadcastRaidNotificationAsync(raid3, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, readySeconds));
		await BroadcastRaidNotificationAsync(raid3, NotiPacketTypeA21.PREPARE_START_RAID, Array.Empty<byte>());
		ScheduleRaidTimer(
			raid3,
			0u,
			0u,
			"preparation",
			readySeconds,
			projectSetTimer: true,
			remainTimeState: 0,
			(current, _) => CompleteRaidPreparationAsync(current));
	}

	private async Task HandleStartNextRaidPhaseAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot prepared = null;
		bool ok = IsAntonPhaseTwoStartRequest(body) && TryResolveUserId(session, out userId) && _raids.TryPrepareNextPhase(userId, out prepared, (IReadOnlyList<RaidMember> members) => PreparationPartyOrder?.Invoke(members));
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] START_RAID_PHASE2 rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		CancelTimer(prepared, 0u, 1u);
		await PrepareAndStartAntonPhaseTwoAsync(prepared, $"leader:{userId}");
	}

	internal static bool IsAntonPhaseTwoStartRequest(byte[] body)
	{
		if (body != null)
		{
			return body.Length == 0;
		}
		return true;
	}

	private async Task PrepareAndStartAntonPhaseTwoAsync(RaidSnapshot prepared, string reason)
	{
		await BroadcastRaidObjectAsync(prepared);
		uint readySeconds = AntonRaidRewardProvider.GetStartDelaySeconds();
		await BroadcastRaidNotificationAsync(prepared, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, readySeconds));
		await BroadcastRaidNotificationAsync(prepared, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, readySeconds));
		await BroadcastRaidNotificationAsync(prepared, NotiPacketTypeA21.PREPARE_START_RAID, Array.Empty<byte>());
		FileLogger.Log($"[GameProtocol] START_RAID_PHASE2_READY raid={prepared.RaidId} reason={reason} seconds={readySeconds}");
		ScheduleRaidTimer(
			prepared,
			0u,
			0u,
			"phase2-preparation",
			readySeconds,
			projectSetTimer: true,
			remainTimeState: 0,
			(current, _) => CompletePhaseTwoPreparationAsync(current, reason));
	}

	public async Task HandleDungeonLoadedAsync(EnhancedClientSession session)
	{
		DungeonRun run = session?.Player?.CurrentRun;
		if (run == null || !IsAntonRaidDungeon(run.DungeonId) || !TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid) || !IsAntonDungeonForPhase(raid.PhaseIndex, run.DungeonId) || !_raids.TryEnterDungeon(userId, (uint)run.DungeonId, out var raid2, out var memberKeys))
		{
			return;
		}
		await Task.Delay(300);
		if (session.Player.CurrentRun == run && TryGetCurrentRaid(raid2, out var current) && current.Members.Any((RaidMember m) => m.UserId == userId && m.SessionId == session.SessionId) && _sessions.TryGet(session.Player.CharacterId, out var session2) && session2 == session)
		{
			if ((long)run.DungeonId == 219)
			{
				await SyncBlackVolcanoBarrierStateAsync(session, raid2);
			}
			ResetRaidMonsterRuntimeValues(raid2, userId, (uint)run.DungeonId);
			await BroadcastRaidParticipationEnterAsync(raid2, (uint)run.DungeonId, memberKeys);
			await BroadcastRaidMonsterStatusAsync(raid2);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ENTER raid={raid2.RaidId} phase={raid2.PhaseIndex} dungeon={run.DungeonId} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	public async Task HandleDungeonClearedAsync(EnhancedClientSession session, int dungeonId)
	{
		if (!IsAntonRaidDungeon(dungeonId) || !TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid) || !IsAntonDungeonForPhase(raid.PhaseIndex, dungeonId) || !_raids.TryClearDungeon(userId, (uint)dungeonId, GetAntonRequiredClears((uint)dungeonId), out var raid2, out var memberKeys, out var clearCount))
		{
			return;
		}
		bool flag = raid2.PhaseIndex == 1;
		await BroadcastRaidMembersAsync(raid2);
		if (flag)
		{
			flag = dungeonId switch
			{
				220 => clearCount >= 5,
				219 => true,
				_ => false,
			};
		}
		if (flag)
		{
			await SetDungeonStateAsync(raid2, (uint)dungeonId, 3u);
		}
		await BroadcastRaidNotificationAsync(raid2, NotiPacketTypeA21.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo((uint)dungeonId, 4u, memberKeys));
		if (raid2.PhaseIndex == 0)
		{
			switch ((uint)dungeonId)
			{
			case 210u:
				await ClearBlackFogSourceAsync(raid2, clearCount);
				break;
			case 211u:
				await ClearRegeneratedBlackFogAsync(raid2);
				break;
			case 212u:
			case 214u:
				await ClearQuakeAsync(raid2, (uint)dungeonId);
				break;
			case 213u:
			case 215u:
				await ClearLegAsync(raid2, (uint)dungeonId, clearCount);
				break;
			case 216u:
				await ClearNavalCannonAsync(raid2);
				break;
			}
		}
		else
		{
			switch ((uint)dungeonId)
			{
			case 218u:
				await ClearEnergyAsync(raid2);
				break;
			case 219u:
				await ClearBlackVolcanoAsync(raid2);
				break;
			case 220u:
				await ClearAntonHeartAsync(raid2, clearCount);
				break;
			default:
				if (AntonRaidRewardProvider.GetHatcheryIndex((uint)dungeonId) >= 0)
				{
					await ClearHatcheryAsync(raid2, (uint)dungeonId);
				}
				break;
			}
		}
		await BroadcastRaidMonsterStatusAsync(raid2);
		FileLogger.Log($"[GameProtocol] RAID_DUNGEON_CLEAR raid={raid2.RaidId} phase={raid2.PhaseIndex} dungeon={dungeonId} clear={clearCount}/{GetAntonRequiredClears((uint)dungeonId)} members={string.Join(",", memberKeys)}");
	}

	public async Task HandleDungeonCharacterDeathAsync(EnhancedClientSession session, int dungeonId)
	{
		if (IsAntonRaidDungeon(dungeonId) && TryResolveUserId(session, out var userId) && _raids.TryGetByUser(userId, out var raid) && IsAntonDungeonForPhase(raid.PhaseIndex, dungeonId) && _raids.TryRecordDeath(userId, out var raid2))
		{
			RaidMember raidMember = raid2.Members.FirstOrDefault((RaidMember member) => member.UserId == userId);
			if (raidMember != null)
			{
				await BroadcastRaidNotificationAsync(raid2, NotiPacketTypeA21.RAID_MEMBER_STATE, RaidPacketBuilder.BuildRaidMemberState(raidMember.UserId, 0));
				FileLogger.Log($"[GameProtocol] RAID_PHASE_DEATH raid={raid2.RaidId} phase={raid2.PhaseIndex} user={userId} dungeon={dungeonId} deaths={raid2.PhaseDeathCount}");
			}
		}
	}

	public async Task HandleDungeonCharacterReviveAsync(EnhancedClientSession session, int dungeonId, ushort targetActorId)
	{
		if (IsAntonRaidDungeon(dungeonId) && TryResolveUserId(session, out var usingUserId) && _raids.TryGetByUser(usingUserId, out var raid) && IsAntonDungeonForPhase(raid.PhaseIndex, dungeonId) && raid.Members.Any((RaidMember member) => member.UserId == targetActorId) && _raids.TryRecordCoinUse(usingUserId, checked((uint)dungeonId), out var raid2))
		{
			ushort targetUserId = targetActorId;
			RaidMember raidMember = raid2.Members.First((RaidMember member) => member.UserId == targetUserId);
			await BroadcastRaidNotificationAsync(raid2, NotiPacketTypeA21.RAID_MEMBER_STATE, RaidPacketBuilder.BuildRaidMemberState(raidMember.UserId, 1));
			await BroadcastRaidMonsterStatusAsync(raid2);
			FileLogger.Log($"[GameProtocol] RAID_PHASE_REVIVE raid={raid2.RaidId} phase={raid2.PhaseIndex} user={targetUserId} actor={targetActorId} coinUser={usingUserId} dungeon={dungeonId}");
		}
	}
	private async Task ClearBlackFogSourceAsync(RaidSnapshot raid, uint count)
	{
		await SetSymbolAsync(raid, 50u, count);
		if (count >= 4)
		{
			await SetDungeonStateAsync(raid, 210u, 3u);
			KeyValuePair<uint, uint>[] antonFirstPhaseSmokeClearedStates = AntonFirstPhaseSmokeClearedStates;
			for (int i = 0; i < antonFirstPhaseSmokeClearedStates.Length; i++)
			{
				KeyValuePair<uint, uint> keyValuePair = antonFirstPhaseSmokeClearedStates[i];
				await SetDungeonStateAsync(raid, keyValuePair.Key, keyValuePair.Value);
			}
			await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
			{
				new KeyValuePair<uint, uint>(120u, 1u),
				new KeyValuePair<uint, uint>(122u, 1u)
			});
			uint quakeAActiveSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, 212u);
			uint quakeBActiveSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, 214u);
			uint blackFogRecoverySeconds = _timerConfiguration.GetDungeonRecoverySeconds(0u, 211u);
			uint blackFogActiveSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, 211u);
			await StartActiveTimerAsync(raid, 212u, quakeAActiveSeconds, ResetPhaseOneAsync);
			await StartActiveTimerAsync(raid, 214u, quakeBActiveSeconds, ResetPhaseOneAsync);
			await StartRecoveryTimerAsync(raid, 211u, blackFogRecoverySeconds, blackFogActiveSeconds, ResetPhaseOneAsync);
			StartNavalCannonReserveOpenTimer(raid);
		}
	}

	private async Task ClearRegeneratedBlackFogAsync(RaidSnapshot raid)
	{
		CancelTimer(raid, 1u, 211u);
		CancelTimer(raid, 3u, 211u);
		await SetSymbolAsync(raid, 1u, 0u);
		await SetDungeonStateAsync(raid, 211u, 3u);
		uint recoverySeconds = _timerConfiguration.GetDungeonRecoverySeconds(0u, 211u);
		uint activeSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, 211u);
		await StartRecoveryTimerAsync(raid, 211u, recoverySeconds, activeSeconds, ResetPhaseOneAsync);
	}

	private async Task ClearQuakeAsync(RaidSnapshot raid, uint dungeonId)
	{
		CancelTimer(raid, 1u, dungeonId);
		await SetDungeonStateAsync(raid, dungeonId, 3u);
		uint recoverySeconds = _timerConfiguration.GetDungeonRecoverySeconds(0u, dungeonId);
		uint activeSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, dungeonId);
		await StartRecoveryTimerAsync(raid, dungeonId, recoverySeconds, activeSeconds, ResetPhaseOneAsync);
	}

	private async Task ClearLegAsync(RaidSnapshot raid, uint dungeonId, uint count)
	{
		await SetSymbolAsync(raid, GetAntonHpSymbolId(dungeonId), count);
		if (count >= 2)
		{
			await SetDungeonStateAsync(raid, dungeonId, 3u);
			uint dungeonId2 = ((dungeonId == 213) ? 215u : 213u);
			_raids.TryGetClearCount(raid.RaidId, dungeonId2, out var clearCount);
			if (clearCount >= 2)
			{
				await CompletePhaseOneAsync(raid);
			}
		}
	}

	private async Task ClearNavalCannonAsync(RaidSnapshot raid)
	{
		CancelTimer(raid, 1u, 216u);
		CancelTimer(raid, 3u, 216u);
		await SetDungeonStateAsync(raid, 216u, 3u);
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(124u, 1u),
			new KeyValuePair<uint, uint>(2u, 0u)
		});
		uint recoverySeconds = _timerConfiguration.GetDungeonRecoverySeconds(0u, 216u);
		uint activeSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, 216u);
		await StartRecoveryTimerAsync(raid, 216u, recoverySeconds, activeSeconds, NavalCannonTimeoutAsync);
	}

	private void StartNavalCannonReserveOpenTimer(RaidSnapshot raid)
	{
		uint reserveOpenSeconds = _timerConfiguration.GetReservedOpenSeconds(0u, 216u);
		uint activeSeconds = _timerConfiguration.GetDungeonActiveSeconds(0u, 216u);
		ScheduleRaidTimer(
			raid,
			0u,
			216u,
			"reserve-open",
			reserveOpenSeconds,
			projectSetTimer: false,
			remainTimeState: null,
			async (current, _) =>
			{
				await SetDungeonStateAsync(current, 216u, 0u);
				await SetSymbolAsync(current, AntonNavigunOnMovieSymbolId, 1u);
				await StartNavalCannonMeteoTimerAsync(current);
				await StartActiveTimerAsync(current, 216u, activeSeconds, NavalCannonTimeoutAsync);
			});
	}

	private async Task NavalCannonTimeoutAsync(RaidSnapshot raid)
	{
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[4]
		{
			new KeyValuePair<uint, uint>(125u, 1u),
			new KeyValuePair<uint, uint>(102u, 1u),
			new KeyValuePair<uint, uint>(GetAntonHpSymbolId(213u), 0u),
			new KeyValuePair<uint, uint>(GetAntonHpSymbolId(215u), 0u)
		});
		_raids.ResetClearCounts(raid.RaidId, new uint[4] { 212u, 213u, 214u, 215u });
		await SetDungeonStateAsync(raid, 212u, 0u);
		await SetDungeonStateAsync(raid, 213u, 0u);
		await SetDungeonStateAsync(raid, 214u, 0u);
		await SetDungeonStateAsync(raid, 215u, 0u);
		await StartActiveTimerAsync(
			raid,
			212u,
			_timerConfiguration.GetDungeonActiveSeconds(0u, 212u),
			ResetPhaseOneAsync);
		await StartActiveTimerAsync(
			raid,
			214u,
			_timerConfiguration.GetDungeonActiveSeconds(0u, 214u),
			ResetPhaseOneAsync);
		await StartActiveTimerAsync(
			raid,
			216u,
			_timerConfiguration.GetDungeonActiveSeconds(0u, 216u),
			NavalCannonTimeoutAsync);
	}

	private async Task ResetPhaseOneAsync(RaidSnapshot raid)
	{
		CancelAllPhaseOneTimers(raid);
		_raids.ResetClearCounts(raid.RaidId, AntonFirstPhaseDungeonIds);
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(104u, 1u),
			new KeyValuePair<uint, uint>(102u, 1u)
		});
		await SetSymbolsAsync(raid, AntonFirstPhaseResetSymbols);
		KeyValuePair<uint, uint>[] antonFirstPhaseInitialDungeonStates = AntonFirstPhaseInitialDungeonStates;
		for (int i = 0; i < antonFirstPhaseInitialDungeonStates.Length; i++)
		{
			KeyValuePair<uint, uint> keyValuePair = antonFirstPhaseInitialDungeonStates[i];
			if (keyValuePair.Key != 210)
			{
				await SetDungeonStateAsync(raid, keyValuePair.Key, keyValuePair.Value);
			}
		}
		await SetDungeonStateAsync(raid, 210u, 0u);
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_FAIL_RESET raid={raid.RaidId}");
	}

	private async Task ClearEnergyAsync(RaidSnapshot raid)
	{
		await SetDungeonStateAsync(raid, 218u, 0u);
		await SetSymbolAsync(raid, GetAntonHpSymbolId(218u), 0u);
		_raids.ResetClearCounts(raid.RaidId, new uint[1] { 218u });
	}

	private async Task ClearBlackVolcanoAsync(RaidSnapshot raid)
	{
		await Task.Delay(2000);
		if (!TryGetCurrentRaid(raid, out var current) || current.State != 2 || current.PhaseIndex != raid.PhaseIndex)
			return;
		CancelTimer(current, 3u, 219u);
		CancelTimer(current, 4u, 219u);
		_infectionDungeonByRaid.TryRemove(current.InstanceId, out var _);
		await SetSymbolsAsync(current, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(7u, 0u),
			new KeyValuePair<uint, uint>(8u, 0u)
		});
		uint[] array = new uint[2] { 218u, 219u };
		uint[] array2 = array;
		foreach (uint dungeonId in array2)
		{
			await SetDungeonStateAsync(current, dungeonId, 2u);
		}
		await SetDungeonStateAsync(current, 220u, 0u);
		array = AntonRaidRewardProvider.GetHatcheryDungeonIds();
		array2 = array;
		foreach (uint hatcheryId in array2)
		{
			for (uint num = 1u; num <= 3; num++)
			{
				CancelTimer(raid, num, hatcheryId);
			}
			await SetDungeonStateAsync(current, hatcheryId, 2u);
			await SetSymbolsAsync(current, new KeyValuePair<uint, uint>[2]
			{
				new KeyValuePair<uint, uint>(GetAntonHatcheryFailSymbolId(hatcheryId), 0u),
				new KeyValuePair<uint, uint>(GetAntonHatcheryNamedSymbolId(hatcheryId), 0u)
			});
		}
	}

	private async Task ClearAntonHeartAsync(RaidSnapshot raid, uint clearCount)
	{
		await SetSymbolAsync(raid, GetAntonHpSymbolId(220u), clearCount);
		if (clearCount >= 5)
		{
			await Task.Delay(2000);
			if (TryGetCurrentRaid(raid, out var current) && current.State == 2 && current.PhaseIndex == raid.PhaseIndex)
				await CompletePhaseTwoAsync(current);
		}
	}

	private async Task ClearHatcheryAsync(RaidSnapshot raid, uint dungeonId)
	{
		CancelTimer(raid, 1u, dungeonId);
		CancelTimer(raid, 3u, dungeonId);
		if (!_infectionDungeonByRaid.TryGetValue(raid.InstanceId, out var infectionDungeonId) || infectionDungeonId != dungeonId)
		{
			await SetDungeonStateAsync(raid, dungeonId, 3u);
		}
		else
		{
			await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[1]
			{
				new KeyValuePair<uint, uint>(7u, 0u)
			});
			await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(dungeonId, 3u, infectionDungeonId));
		}
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(GetAntonHatcheryFailSymbolId(dungeonId), 0u),
			new KeyValuePair<uint, uint>(GetAntonHatcheryNamedSymbolId(dungeonId), 0u)
		});
		await StartHatcheryRecoveryTimerAsync(raid, dungeonId);
	}

	private async Task CompletePhaseTwoAsync(RaidSnapshot raid)
	{
		if (_raids.TryEnterPhaseBreak(raid, out var result))
		{
			CancelTimer(result, 0u, 0u);
			CancelAllPhaseTwoTimers(result);
			uint[] antonSecondPhaseDungeonIds = AntonSecondPhaseDungeonIds;
			for (int i = 0; i < antonSecondPhaseDungeonIds.Length; i++)
			{
				await SetDungeonStateAsync(dungeonId: antonSecondPhaseDungeonIds[i], raid: result, state: 2u);
			}
			ushort[] eligibleUserIds = (from member in result.Members
				where _raids.HasClearedDungeon(result.RaidId, member.UserId)
				select member.UserId).ToArray();
			_phaseRewardFlows[result.InstanceId] = new PhaseRewardFlow(eligibleUserIds);
			await StartPhaseOneResultMovieAsync(result);
		}
	}

	private void StartBarrierRecoveryTimer(RaidSnapshot raid)
	{
		ScheduleBarrierRecoveryTick(raid, retainedVersion: null);
	}

	private void ScheduleBarrierRecoveryTick(RaidSnapshot raid, Guid? retainedVersion)
	{
		ScheduleRaidTimer(
			raid,
			4u,
			219u,
			"barrier-recovery",
			1u,
			projectSetTimer: false,
			remainTimeState: null,
			async (current, version) =>
			{
				bool infectionActive = _infectionDungeonByRaid.ContainsKey(current.InstanceId)
					&& _symbolValues.TryGetValue((current.InstanceId, AntonInfectionExistsSymbolId), out var value)
					&& value != 0;
				await ChangeBlackVolcanoBarrierAsync(
					current,
					AntonRaidRewardProvider.GetShieldChargeRate(infectionActive),
					1,
					infectionActive ? "infection-recovery" : "normal-recovery");
				if (_symbolValues.TryGetValue((current.InstanceId, AntonBlackVolcanoBarrierSymbolId), out uint barrier)
					&& barrier < AntonBlackVolcanoBarrierMaximum
					&& TimerCurrent(current, 4u, 219u, "barrier-recovery", version))
				{
					ScheduleBarrierRecoveryTick(current, version);
				}
			},
			retainedVersion);
	}

	private async Task ChangeBlackVolcanoBarrierAsync(RaidSnapshot raid, uint operand, byte operation, string reason)
	{
		(Guid, uint) key = (raid.InstanceId, 110u);
		uint previousValue;
		uint nextValue;
		lock (_raidRuntimeLocks.GetOrAdd(raid.InstanceId, (Guid _) => new object()))
		{
			if (!_symbolValues.TryGetValue(key, out previousValue) || !TryApplyRaidSymbolOperation(previousValue, operand, operation, out nextValue))
			{
				return;
			}
			if (nextValue > 10000)
			{
				nextValue = 10000u;
			}
			if (nextValue == previousValue)
			{
				return;
			}
			_symbolValues[key] = nextValue;
		}
		if (nextValue == 0 && previousValue != 0)
		{
			_blackVolcanoBarrierBroken[raid.InstanceId] = 1;
			StartBarrierRecoveryTimer(raid);
			await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[4]
			{
				new KeyValuePair<uint, uint>(127u, 0u),
				new KeyValuePair<uint, uint>(110u, 0u),
				new KeyValuePair<uint, uint>(111u, 1u),
				new KeyValuePair<uint, uint>(126u, 1u)
			});
			FileLogger.Log($"[GameProtocol] RAID_SYMBOL_PULSE raid={raid.RaidId} symbol={126u}");
		}
		else if (nextValue != 10000 || previousValue == 10000)
		{
			await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbol(110u, nextValue));
		}
		else
		{
			_blackVolcanoBarrierBroken[raid.InstanceId] = 0;
			await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[4]
			{
				new KeyValuePair<uint, uint>(126u, 0u),
				new KeyValuePair<uint, uint>(110u, 10000u),
				new KeyValuePair<uint, uint>(111u, 0u),
				new KeyValuePair<uint, uint>(127u, 1u)
			});
			FileLogger.Log($"[GameProtocol] RAID_SYMBOL_PULSE raid={raid.RaidId} symbol={127u}");
		}
		if (!reason.EndsWith("recovery", StringComparison.Ordinal))
		{
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_BARRIER raid={raid.RaidId} reason={reason} old={previousValue} operand={operand} operation={operation} value={nextValue}");
		}
	}

	private async Task PulseSymbolAsync(RaidSnapshot raid, uint symbolId)
	{
		await SetSymbolAsync(raid, symbolId, 0u);
		await SetSymbolAsync(raid, symbolId, 1u);
		FileLogger.Log($"[GameProtocol] RAID_SYMBOL_PULSE raid={raid.RaidId} symbol={symbolId}");
	}

	private async Task SyncBlackVolcanoBarrierStateAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		_symbolValues.TryGetValue((raid.InstanceId, 110u), out var barrierValue);
		bool barrierBroken = _blackVolcanoBarrierBroken.TryGetValue(raid.InstanceId, out var value) && value != 0;
		uint activeMovieSymbol = (barrierBroken ? 126u : 127u);
		uint key = (barrierBroken ? 127u : 126u);
		byte[] data = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(new KeyValuePair<uint, uint>[4]
		{
			new KeyValuePair<uint, uint>(key, 0u),
			new KeyValuePair<uint, uint>(110u, barrierValue),
			new KeyValuePair<uint, uint>(111u, barrierBroken ? 1u : 0u),
			new KeyValuePair<uint, uint>(activeMovieSymbol, 1u)
		}));
		await session.SendPacketAsync(data);
		FileLogger.Log($"[GameProtocol] RAID_VOLCANO_SYNC raid={raid.RaidId} barrier={barrierValue} broken={barrierBroken} symbol={activeMovieSymbol}");
	}

	private async Task StartHatcheryOpenTimerAsync(RaidSnapshot raid)
	{
		uint openSeconds = _timerConfiguration.GetHatcheryOpenSeconds();
		ScheduleRaidTimer(
			raid,
			3u,
			219u,
			"hatchery-open",
			openSeconds,
			projectSetTimer: true,
			remainTimeState: null,
			async (current, _) =>
			{
				uint[] hatcheryDungeonIds = AntonRaidRewardProvider.GetHatcheryDungeonIds();
				int omittedIndex = Random.Shared.Next(hatcheryDungeonIds.Length);
				uint[] openHatcheries = SelectAntonOpenHatcheries(omittedIndex);
				uint infectionDungeonId = SelectAntonInfectionHatchery(
					openHatcheries,
					Random.Shared.Next(openHatcheries.Length));
				_infectionDungeonByRaid[current.InstanceId] = infectionDungeonId;
				_symbolValues[(current.InstanceId, AntonInfectionExistsSymbolId)] = 1u;
				_symbolValues[(current.InstanceId, AntonInfectionDungeonIndexSymbolId)] = infectionDungeonId;
				await SetSymbolAsync(current, AntonHatcheryOpenMovieSymbolId, 1u);
				await BroadcastRaidNotificationAsync(
					current,
					NotiPacketTypeA21.RAID_DUNGEON_STATE,
					RaidPacketBuilder.BuildDungeonState(
						openHatcheries.Select(id => new KeyValuePair<uint, uint>(id, 0u)).ToArray(),
						infectionDungeonId));
				foreach (uint dungeonId in openHatcheries)
					await StartHatcheryEffectTimersAsync(current, dungeonId);
				FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERIES_OPEN raid={current.RaidId} omitted={hatcheryDungeonIds[omittedIndex]} infection={infectionDungeonId}");
			});
		await SendTimerAsync(raid, 3u, 219u, openSeconds);
	}

	internal static uint[] SelectAntonOpenHatcheries(int omittedIndex)
	{
		uint[] hatcheryDungeonIds = AntonRaidRewardProvider.GetHatcheryDungeonIds();
		if (omittedIndex < 0 || omittedIndex >= hatcheryDungeonIds.Length)
		{
			throw new ArgumentOutOfRangeException("omittedIndex");
		}
		int count = checked((int)AntonRaidRewardProvider.GetHatcheryOpenCount());
		return hatcheryDungeonIds.Where((uint _, int index) => index != omittedIndex).Take(count).ToArray();
	}

	internal static uint SelectAntonInfectionHatchery(IReadOnlyList<uint> openHatcheries, int selectedIndex)
	{
		if (openHatcheries == null || selectedIndex < 0 || selectedIndex >= openHatcheries.Count)
		{
			throw new ArgumentOutOfRangeException("selectedIndex");
		}
		return openHatcheries[selectedIndex];
	}

	private Task StartHatcheryEffectTimersAsync(RaidSnapshot raid, uint dungeonId)
	{
		StartRepeatingHatcherySymbolTimer(
			raid,
			dungeonId,
			1u,
			_timerConfiguration.GetHatcheryEffectInitialSeconds(1u, dungeonId),
			_timerConfiguration.GetHatcheryEffectRepeatSeconds(1u, dungeonId),
			GetAntonHatcheryFailSymbolId(dungeonId));
		StartRepeatingHatcherySymbolTimer(
			raid,
			dungeonId,
			3u,
			_timerConfiguration.GetHatcheryEffectInitialSeconds(3u, dungeonId),
			_timerConfiguration.GetHatcheryEffectRepeatSeconds(3u, dungeonId),
			GetAntonHatcheryNamedSymbolId(dungeonId));
		return Task.CompletedTask;
	}

	private void StartRepeatingHatcherySymbolTimer(RaidSnapshot raid, uint dungeonId, uint timerType, uint initialSeconds, uint repeatSeconds, uint symbolId)
	{
		ScheduleRepeatingHatcherySymbolTimer(
			raid,
			dungeonId,
			timerType,
			initialSeconds,
			repeatSeconds,
			symbolId,
			retainedVersion: null);
		RunInBackground(SendTimerAsync(raid, timerType, dungeonId, initialSeconds), "hatchery-symbol-send-timer");
	}

	private void ScheduleRepeatingHatcherySymbolTimer(
		RaidSnapshot raid,
		uint dungeonId,
		uint timerType,
		uint seconds,
		uint repeatSeconds,
		uint symbolId,
		Guid? retainedVersion)
	{
		ScheduleRaidTimer(
			raid,
			timerType,
			dungeonId,
			"hatchery-effect",
			seconds,
			projectSetTimer: true,
			remainTimeState: null,
			async (current, version) =>
			{
				await SetSymbolAsync(current, symbolId, 1u);
				if (!TimerCurrent(current, timerType, dungeonId, "hatchery-effect", version))
					return;
				ScheduleRepeatingHatcherySymbolTimer(
					current,
					dungeonId,
					timerType,
					repeatSeconds,
					repeatSeconds,
					symbolId,
					version);
				await SendTimerAsync(current, timerType, dungeonId, repeatSeconds);
			},
			retainedVersion);
	}

	private async Task StartHatcheryRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId)
	{
		uint recoverySeconds = _timerConfiguration.GetDungeonRecoverySeconds(1u, dungeonId);
		ScheduleRaidTimer(
			raid,
			2u,
			dungeonId,
			"recovery",
			recoverySeconds,
			projectSetTimer: true,
			remainTimeState: null,
			async (current, _) =>
			{
				_raids.ResetClearCounts(current.RaidId, new uint[1] { dungeonId });
				await SetSymbolAsync(current, GetAntonHpSymbolId(dungeonId), 0u);
				if (!_infectionDungeonByRaid.TryGetValue(current.InstanceId, out uint infectionDungeonId)
					|| infectionDungeonId != dungeonId)
				{
					await SetDungeonStateAsync(current, dungeonId, 0u);
				}
				else
				{
					await SetSymbolAsync(current, AntonInfectionExistsSymbolId, 1u);
					await BroadcastRaidNotificationAsync(
						current,
						NotiPacketTypeA21.RAID_DUNGEON_STATE,
						RaidPacketBuilder.BuildDungeonState(dungeonId, 0u, infectionDungeonId));
				}
				await StartHatcheryEffectTimersAsync(current, dungeonId);
			});
		await SendTimerAsync(raid, 2u, dungeonId, recoverySeconds);
	}

	private static uint GetAntonHatcheryFailSymbolId(uint dungeonId)
	{
		int hatcheryIndex = AntonRaidRewardProvider.GetHatcheryIndex(dungeonId);
		if (hatcheryIndex < 0)
		{
			throw new ArgumentOutOfRangeException("dungeonId");
		}
		return checked((uint)(3 + hatcheryIndex));
	}

	private static uint GetAntonHatcheryNamedSymbolId(uint dungeonId)
	{
		int hatcheryIndex = AntonRaidRewardProvider.GetHatcheryIndex(dungeonId);
		if (hatcheryIndex < 0)
		{
			throw new ArgumentOutOfRangeException("dungeonId");
		}
		return checked((uint)(9 + hatcheryIndex));
	}

	private async Task CompletePhaseOneAsync(RaidSnapshot raid)
	{
		if (_raids.TryEnterPhaseBreak(raid, out var waiting))
		{
			CancelTimer(waiting, 0u, 0u);
			CancelAllPhaseOneTimers(waiting);
			uint[] antonFirstPhaseDungeonIds = AntonFirstPhaseDungeonIds;
			for (int i = 0; i < antonFirstPhaseDungeonIds.Length; i++)
			{
				await SetDungeonStateAsync(dungeonId: antonFirstPhaseDungeonIds[i], raid: waiting, state: 2u);
			}
			await SetSymbolsAsync(waiting, new KeyValuePair<uint, uint>[2]
			{
				new KeyValuePair<uint, uint>(1u, 0u),
				new KeyValuePair<uint, uint>(2u, 0u)
			});
			ushort[] eligibleUserIds = (from member in waiting.Members
				where _raids.HasClearedDungeon(waiting.RaidId, member.UserId)
				select member.UserId).ToArray();
			_phaseRewardFlows[waiting.InstanceId] = new PhaseRewardFlow(eligibleUserIds);
			await Task.Delay(2000);
			if (TryGetCurrentRaid(waiting, out var current)
				&& current.State == waiting.State
				&& current.PhaseIndex == waiting.PhaseIndex)
			{
				await SetSymbolAsync(current, 105u, 1u);
				await StartPhaseOneResultMovieAsync(current);
			}
		}
	}

	private async Task CompleteRaidPreparationAsync(RaidSnapshot raid)
	{
		ushort userId = raid.LeaderUserId;
		if (!_sessions.TryGet(checked((int)raid.Leader.CharacterId), out var session) || session.SessionId != raid.Leader.SessionId || !IsRaidSession(session))
		{
			if (_raids.TryCancelPreparation(raid, out var raid2))
			{
				await BroadcastRaidStateAsync(raid2);
			}
			return;
		}
		RaidSnapshot notPrepared;
		if (!_raids.IsPreparationReady(raid, (IReadOnlyList<RaidMember> members) => PreparationPartiesReady?.Invoke(members) ?? false))
		{
			if (_raids.TryCancelPreparation(raid, out notPrepared))
			{
				await BroadcastRaidStateAsync(notPrepared);
				await BroadcastRaidNotificationAsync(notPrepared, NotiPacketTypeA21.RAID_ENTRY_COST_INFO, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(notPrepared)));
				await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice("攻坚小队组建未完成，请确认队员在线且未加入其他队伍后重试。", 0)));
			}
			FileLogger.Log($"[GameProtocol] START_RAID preparation incomplete raid={raid.RaidId} generation={raid.PreparationGeneration}; no materials consumed");
			return;
		}
		List<RaidConsumedEntryCost> consumedCosts = new List<RaidConsumedEntryCost>();
		if (!_raids.TryCompletePreparation(raid, delegate
		{
			Func<IReadOnlyList<RaidMember>, bool> preparationPartiesReady = PreparationPartiesReady;
			return preparationPartiesReady != null && preparationPartiesReady(raid.Members) && TryConsumeEntryCosts(raid, out consumedCosts);
		}, out var started))
		{
			_raids.TryCancelPreparation(raid, out notPrepared);
			if (notPrepared != null)
			{
				await BroadcastRaidStateAsync(notPrepared);
				await BroadcastRaidNotificationAsync(notPrepared, NotiPacketTypeA21.RAID_ENTRY_COST_INFO, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(notPrepared)));
			}
			FileLogger.Log($"[GameProtocol] START_RAID material check failed raid={raid.RaidId} leader={userId}");
			return;
		}
		foreach (RaidConsumedEntryCost item in consumedCosts)
		{
			await InventoryRefreshSender.SendOnlineUpdateItemList(item.Session, InventoryListType.Main, item.SlotIndex);
		}
		await BroadcastRaidObjectAsync(started);
		await BroadcastRaidMembersAsync(started);
		await BroadcastRaidStateAsync(started);
		await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(AntonFirstPhaseInitialDungeonStates));
		await BroadcastRaidSituationAsync(started);
		await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(AntonFirstPhaseInitialSymbols));
		uint phaseLimitSeconds = _timerConfiguration.GetPhaseLimitSeconds(started.PhaseIndex);
		await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, phaseLimitSeconds));
		await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, phaseLimitSeconds));
		StartAttackTimeoutTimer(started, phaseLimitSeconds);
		FileLogger.Log($"[GameProtocol] START_RAID_ATTACK raid={started.RaidId} state={started.State} dungeon={210u} seconds={phaseLimitSeconds}");
	}

	private async Task CompletePhaseTwoPreparationAsync(RaidSnapshot prepared, string reason)
	{
		checked
		{
			RaidSnapshot started;
			if (!prepared.Members.All((RaidMember raidMember) => _sessions.TryGet((int)raidMember.CharacterId, out var session2) && session2.SessionId == raidMember.SessionId && IsRaidSession(session2)) || !_raids.IsPreparationReady(prepared, (IReadOnlyList<RaidMember> members) => PreparationPartiesReady?.Invoke(members) ?? false))
			{
				if (_raids.TryCancelPreparation(prepared, out var cancelled))
				{
					await BroadcastRaidStateAsync(cancelled);
					await BroadcastRaidNotificationAsync(cancelled, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(1, 0u));
					foreach (RaidMember member in cancelled.Members)
					{
						if (_sessions.TryGet((int)member.CharacterId, out var session) && session.SessionId == member.SessionId)
						{
							await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice("第二阶段小队准备未完成，请确认队员在线并完成组队后重新开始。", 0)));
						}
					}
				}
				FileLogger.Log($"[GameProtocol] START_RAID_PHASE2 preparation incomplete raid={prepared.RaidId} generation={prepared.PreparationGeneration}");
			}
			else if (!_raids.TryCompletePreparedNextPhase(prepared, (IReadOnlyList<RaidMember> members) => PreparationPartiesReady?.Invoke(members) ?? false, out started))
			{
				if (_raids.TryCancelPreparation(prepared, out var raid))
				{
					await BroadcastRaidStateAsync(raid);
				}
				FileLogger.Log($"[GameProtocol] START_RAID_PHASE2 aborted raid={prepared.RaidId}");
			}
			else
			{
				await BroadcastRaidObjectAsync(started);
				await BroadcastRaidStateAsync(started);
				_blackVolcanoBarrierBroken[started.InstanceId] = 0;
				await SetSymbolsAsync(started, AntonSecondPhaseInitialSymbols);
				await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(AntonSecondPhaseInitialDungeonStates));
				await BroadcastRaidSituationAsync(started);
				await PulseSymbolAsync(started, 127u);
				uint phaseLimitSeconds = _timerConfiguration.GetPhaseLimitSeconds(started.PhaseIndex);
				await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, phaseLimitSeconds));
				await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, phaseLimitSeconds));
				StartAttackTimeoutTimer(started, phaseLimitSeconds);
				await StartHatcheryOpenTimerAsync(started);
				StartBarrierRecoveryTimer(started);
				FileLogger.Log($"[GameProtocol] START_RAID_PHASE2_ATTACK raid={started.RaidId} reason={reason} seconds={phaseLimitSeconds}");
			}
		}
	}

	private void SchedulePhaseBreakTimer(RaidSnapshot expected, uint seconds)
	{
		ScheduleRaidTimer(
			expected,
			0u,
			1u,
			"phase-break",
			seconds,
			projectSetTimer: true,
			remainTimeState: 1,
			async (current, _) =>
			{
				if (_raids.TryPrepareNextPhaseAutomatically(
					current,
					out var prepared,
					members => PreparationPartyOrder?.Invoke(members)))
				{
					await PrepareAndStartAntonPhaseTwoAsync(prepared, "phase-break-timeout");
				}
			});
	}

	internal static byte[] BuildFailedRaidResultPacket(RaidSnapshot raid)
	{
		if (raid.State != 4 || raid.StateArgument != 1)
		{
			throw new ArgumentException("Raid is not in the failed terminal state.", "raid");
		}
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_RESULT, RaidPacketBuilder.BuildRaidResult(1u, raid.PhaseIndex, raid.PhaseClearTimeSeconds, raid.PhaseDeathCount, 0u, 1));
	}
}
