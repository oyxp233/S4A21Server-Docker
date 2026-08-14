using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	public async Task HandleStartRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		bool emptyBody = body == null || body.Length == 0;
		if (emptyBody && TryResolveUserId(session, out var waitingUserId) && _raids.TryGetByUser(waitingUserId, out var waitingRaid) && waitingRaid.State == 5 && waitingRaid.StateArgument == 0 && waitingRaid.PhaseIndex == 0)
		{
			await HandleStartNextRaidPhaseAsync(session, header, body);
			return;
		}
		if (!emptyBody || !TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var candidate) || !HasAllEntryCosts(candidate) || !_raids.TryBeginStart(userId, out var raid))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		await SendAckAsync(session, header.type, success: true);
		FileLogger.Log($"[GameProtocol] START_RAID_READY raid={raid.RaidId} leader={userId}");
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, 3u));
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, 3u));
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.PREPARE_START_RAID, Array.Empty<byte>());
		await Task.Delay(3000);
		if (!TryConsumeEntryCosts(raid, out var consumedCosts))
		{
			_raids.TryCancelStart(raid.RaidId, userId, out var cancelled);
			if (cancelled != null)
			{
				await BroadcastRaidNotificationAsync(cancelled, NotiPacketType.RAID_ENTRY_COST_INFO, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(cancelled)));
			}
			FileLogger.Log($"[GameProtocol] START_RAID material check failed raid={raid.RaidId} leader={userId}");
			return;
		}
		if (!_raids.TryCompleteStart(raid.RaidId, userId, out var started))
		{
			FileLogger.Log($"[GameProtocol] START_RAID aborted raid={raid.RaidId} leader={userId}");
			return;
		}
		foreach (RaidConsumedEntryCost consumed in consumedCosts)
		{
			await InventoryRefreshSender.SendOnlineUpdateItemList(consumed.Session, InventoryListType.Main, consumed.SlotIndex);
		}
		await BroadcastRaidStateAsync(started);
		// Publish the final party assignment once at attack start. The client uses
		// this operation=3 cache to build situation rows after entering a dungeon.
		await BroadcastRaidObjectAsync(started);
		await BroadcastRaidMembersAsync(started);
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(AntonFirstPhaseInitialDungeonStates));
		await BroadcastRaidSituationAsync(started);
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(AntonFirstPhaseInitialSymbols));
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, AttackSeconds));
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, AttackSeconds));
		StartAttackTimeoutTimer(started, AttackSeconds);
		FileLogger.Log($"[GameProtocol] START_RAID_ATTACK raid={started.RaidId} state={started.State} dungeon={210u} seconds={AttackSeconds}");
	}

	private async Task HandleStartNextRaidPhaseAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot prepared = null;
		bool ok = IsAntonPhaseTwoStartRequest(body) && TryResolveUserId(session, out userId) && _raids.TryPrepareNextPhase(userId, out prepared);
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] START_RAID_PHASE2 rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		CancelTimer(prepared.RaidId, 0u, 1u);
		await PrepareAndStartAntonPhaseTwoAsync(prepared, $"leader:{userId}");
	}

	internal static bool IsAntonPhaseTwoStartRequest(byte[] body)
	{
		return body == null || body.Length == 0;
	}

	private async Task PrepareAndStartAntonPhaseTwoAsync(RaidSnapshot prepared, string reason)
	{
		uint readySeconds = AntonRaidRewardProvider.GetStartDelaySeconds();
		await BroadcastRaidNotificationAsync(prepared, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, readySeconds));
		await BroadcastRaidNotificationAsync(prepared, NotiPacketType.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, readySeconds));
		await BroadcastRaidNotificationAsync(prepared, NotiPacketType.PREPARE_START_RAID, Array.Empty<byte>());
		FileLogger.Log($"[GameProtocol] START_RAID_PHASE2_READY raid={prepared.RaidId} reason={reason} seconds={readySeconds}");
		await Task.Delay(checked((int)readySeconds * 1000));
		if (!_raids.TryCompletePreparedNextPhase(prepared.RaidId, out var started))
		{
			FileLogger.Log($"[GameProtocol] START_RAID_PHASE2 aborted raid={prepared.RaidId}");
			return;
		}
		await BroadcastRaidStateAsync(started);
		_blackVolcanoBarrierBroken[started.RaidId] = 0;
		await SetSymbolsAsync(started, AntonSecondPhaseInitialSymbols);
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(AntonSecondPhaseInitialDungeonStates));
		await BroadcastRaidSituationAsync(started);
		await PulseSymbolAsync(started, 127u);
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, AttackSeconds));
		await BroadcastRaidNotificationAsync(started, NotiPacketType.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, AttackSeconds));
		StartAttackTimeoutTimer(started, AttackSeconds);
		await StartHatcheryOpenTimerAsync(started);
		StartBarrierRecoveryTimer(started);
		FileLogger.Log($"[GameProtocol] START_RAID_PHASE2_ATTACK raid={started.RaidId} reason={reason} seconds={AttackSeconds}");
	}

	private async Task RunPhaseBreakTimerAsync(uint raidId, uint seconds)
	{
		int version = AdvanceTimer(raidId, 0u, 1u);
		try
		{
			await Task.Delay(checked((int)seconds * 1000));
			if (TimerCurrent(raidId, 0u, 1u, version) && _raids.TryPrepareNextPhaseAutomatically(raidId, out var prepared))
			{
				await PrepareAndStartAntonPhaseTwoAsync(prepared, "phase-break-timeout");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_PHASE_BREAK_TIMER failed raid={raidId} error={ex2.Message}");
		}
	}

	public async Task HandleDungeonLoadedAsync(EnhancedClientSession session)
	{
		DungeonRun run = session?.Player?.CurrentRun;
		if (run != null && IsAntonRaidDungeon(run.DungeonId) && TryResolveUserId(session, out var userId) && _raids.TryGetByUser(userId, out var currentRaid) && IsAntonDungeonForPhase(currentRaid.PhaseIndex, run.DungeonId) && _raids.TryEnterDungeon(userId, (uint)run.DungeonId, out var raid, out var memberKeys))
		{
			await Task.Delay(300);
			if ((long)run.DungeonId == 219)
			{
				await SyncBlackVolcanoBarrierStateAsync(session, raid);
			}
			ResetRaidMonsterRuntimeValues(raid, userId, (uint)run.DungeonId);
			await BroadcastRaidParticipationEnterAsync(raid, (uint)run.DungeonId, memberKeys);
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ENTER raid={raid.RaidId} phase={raid.PhaseIndex} dungeon={run.DungeonId} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	public async Task HandleDungeonClearedAsync(EnhancedClientSession session, int dungeonId)
	{
		if (!IsAntonRaidDungeon(dungeonId) || !TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var currentRaid) || !IsAntonDungeonForPhase(currentRaid.PhaseIndex, dungeonId) || !_raids.TryClearDungeon(userId, (uint)dungeonId, GetAntonRequiredClears((uint)dungeonId), out var raid, out var memberKeys, out var clearCount))
		{
			return;
		}
		if (raid.PhaseIndex == 1 && dungeonId switch
		{
			220 => clearCount >= 5,
			219 => true,
			_ => false,
		})
		{
			await SetDungeonStateAsync(raid, (uint)dungeonId, 3u);
		}
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo((uint)dungeonId, 4u, memberKeys));
		if (raid.PhaseIndex == 0)
		{
			switch ((uint)dungeonId)
			{
			case 210u:
				await ClearBlackFogSourceAsync(raid, clearCount);
				break;
			case 211u:
				await ClearRegeneratedBlackFogAsync(raid);
				break;
			case 212u:
			case 214u:
				await ClearQuakeAsync(raid, (uint)dungeonId);
				break;
			case 213u:
			case 215u:
				await ClearLegAsync(raid, (uint)dungeonId, clearCount);
				break;
			case 216u:
				await ClearNavalCannonAsync(raid);
				break;
			}
		}
		else
		{
			switch ((uint)dungeonId)
			{
			case 218u:
				await ClearEnergyAsync(raid);
				break;
			case 219u:
				await ClearBlackVolcanoAsync(raid);
				break;
			case 220u:
				await ClearAntonHeartAsync(raid, clearCount);
				break;
			default:
				if (AntonRaidRewardProvider.GetHatcheryIndex((uint)dungeonId) >= 0)
				{
					await ClearHatcheryAsync(raid, (uint)dungeonId);
				}
				break;
			}
		}
		await BroadcastRaidMonsterStatusAsync(raid);
		FileLogger.Log($"[GameProtocol] RAID_DUNGEON_CLEAR raid={raid.RaidId} phase={raid.PhaseIndex} dungeon={dungeonId} clear={clearCount}/{GetAntonRequiredClears((uint)dungeonId)} members={string.Join(",", memberKeys)}");
	}

	public async Task HandleDungeonCharacterDeathAsync(EnhancedClientSession session, int dungeonId)
	{
		if (!IsAntonRaidDungeon(dungeonId)
			|| !TryResolveUserId(session, out var userId)
			|| !_raids.TryGetByUser(userId, out var currentRaid)
			|| !IsAntonDungeonForPhase(currentRaid.PhaseIndex, dungeonId)
			|| !_raids.TryRecordDeath(userId, out var raid))
			return;

		RaidMember deadMember = raid.Members.FirstOrDefault(member => member.UserId == userId);
		if (deadMember == null)
			return;

		await BroadcastRaidNotificationAsync(
			raid,
			NotiPacketType.RAID_MEMBER_STATE,
			RaidPacketBuilder.BuildRaidMemberState(deadMember.UserId, 0));
		FileLogger.Log(
			$"[GameProtocol] RAID_PHASE_DEATH raid={raid.RaidId} phase={raid.PhaseIndex} " +
			$"user={userId} dungeon={dungeonId} deaths={raid.PhaseDeathCount}");
	}

	public async Task HandleDungeonCharacterReviveAsync(
		EnhancedClientSession session,
		int dungeonId,
		ushort targetActorId)
	{
		if (!IsAntonRaidDungeon(dungeonId)
			|| !TryResolveUserId(session, out var usingUserId)
			|| !_raids.TryGetByUser(usingUserId, out var currentRaid)
			|| !IsAntonDungeonForPhase(currentRaid.PhaseIndex, dungeonId)
			|| !currentRaid.Members.Any(member => member.UserId == targetActorId)
			|| !_raids.TryRecordCoinUse(usingUserId, checked((uint)dungeonId), out var raid))
			return;

		ushort targetUserId = targetActorId;
		RaidMember revivedMember = raid.Members.First(member => member.UserId == targetUserId);
		await BroadcastRaidNotificationAsync(
			raid,
			NotiPacketType.RAID_MEMBER_STATE,
			RaidPacketBuilder.BuildRaidMemberState(revivedMember.UserId, 1));
		await BroadcastRaidMonsterStatusAsync(raid);
		FileLogger.Log(
			$"[GameProtocol] RAID_PHASE_REVIVE raid={raid.RaidId} phase={raid.PhaseIndex} " +
			$"user={targetUserId} actor={targetActorId} coinUser={usingUserId} dungeon={dungeonId}");
	}
	private async Task ClearBlackFogSourceAsync(RaidSnapshot raid, uint count)
	{
		if (count < 4)
		{
			await SetSymbolAsync(raid, 50u, count);
			return;
		}
		await SetDungeonStateAsync(raid, 210u, 3u);
		KeyValuePair<uint, uint>[] antonFirstPhaseSmokeClearedStates = AntonFirstPhaseSmokeClearedStates;
		for (int i = 0; i < antonFirstPhaseSmokeClearedStates.Length; i++)
		{
			KeyValuePair<uint, uint> state = antonFirstPhaseSmokeClearedStates[i];
			await SetDungeonStateAsync(raid, state.Key, state.Value);
		}
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(120u, 1u),
			new KeyValuePair<uint, uint>(122u, 1u)
		});
		await StartActiveTimerAsync(raid, 212u, 300u, ResetPhaseOneAsync);
		await StartActiveTimerAsync(raid, 214u, 300u, ResetPhaseOneAsync);
		await StartRecoveryTimerAsync(raid, 211u, 300u, 480u, ResetPhaseOneAsync);
		RunInBackground(OpenNavalCannonAfterDelayAsync(raid), "open-naval-cannon");
	}

	private async Task ClearRegeneratedBlackFogAsync(RaidSnapshot raid)
	{
		CancelTimer(raid.RaidId, 1u, 211u);
		CancelTimer(raid.RaidId, 3u, 211u);
		await SetSymbolAsync(raid, 1u, 0u);
		await SetDungeonStateAsync(raid, 211u, 3u);
		await StartRecoveryTimerAsync(raid, 211u, 300u, 480u, ResetPhaseOneAsync);
	}

	private async Task ClearQuakeAsync(RaidSnapshot raid, uint dungeonId)
	{
		CancelTimer(raid.RaidId, 1u, dungeonId);
		await SetDungeonStateAsync(raid, dungeonId, 3u);
		await StartRecoveryTimerAsync(raid, dungeonId, 150u, 300u, ResetPhaseOneAsync);
	}

	private async Task ClearLegAsync(RaidSnapshot raid, uint dungeonId, uint count)
	{
		if (count < 2)
		{
			await SetSymbolAsync(raid, GetAntonHpSymbolId(dungeonId), count);
			return;
		}
		await SetDungeonStateAsync(raid, dungeonId, 3u);
		uint other = ((dungeonId == 213) ? 215u : 213u);
		_raids.TryGetClearCount(raid.RaidId, other, out var otherCount);
		if (otherCount >= 2)
		{
			await CompletePhaseOneAsync(raid);
		}
	}

	private async Task ClearNavalCannonAsync(RaidSnapshot raid)
	{
		CancelTimer(raid.RaidId, 1u, 216u);
		CancelTimer(raid.RaidId, 3u, 216u);
		await SetDungeonStateAsync(raid, 216u, 3u);
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(124u, 1u),
			new KeyValuePair<uint, uint>(2u, 0u)
		});
		await StartRecoveryTimerAsync(raid, 216u, 150u, 360u, NavalCannonTimeoutAsync);
	}

	private async Task OpenNavalCannonAfterDelayAsync(RaidSnapshot raid)
	{
		try
		{
			await Task.Delay(20000);
			if (TryGetCurrentRaid(raid, out var current))
			{
				await SetDungeonStateAsync(current, 216u, 0u);
				await SetSymbolAsync(current, 123u, 1u);
				await StartNavalCannonMeteoTimerAsync(current);
				await StartActiveTimerAsync(current, 216u, 360u, NavalCannonTimeoutAsync);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_NAVAL_OPEN failed raid={raid?.RaidId} error={ex2.Message}");
		}
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
		await StartActiveTimerAsync(raid, 212u, 300u, ResetPhaseOneAsync);
		await StartActiveTimerAsync(raid, 214u, 300u, ResetPhaseOneAsync);
		await StartActiveTimerAsync(raid, 216u, 360u, NavalCannonTimeoutAsync);
	}

	private async Task ResetPhaseOneAsync(RaidSnapshot raid)
	{
		CancelAllPhaseOneTimers(raid.RaidId);
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
			KeyValuePair<uint, uint> state = antonFirstPhaseInitialDungeonStates[i];
			if (state.Key != 210)
			{
				await SetDungeonStateAsync(raid, state.Key, state.Value);
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
		CancelTimer(raid.RaidId, 3u, 219u);
		CancelTimer(raid.RaidId, 4u, 219u);
		_infectionDungeonByRaid.TryRemove(raid.RaidId, out var _);
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(7u, 0u),
			new KeyValuePair<uint, uint>(8u, 0u)
		});
		uint[] array = new uint[2] { 218u, 219u };
		foreach (uint dungeonId in array)
		{
			await SetDungeonStateAsync(raid, dungeonId, 2u);
		}
		await SetDungeonStateAsync(raid, 220u, 0u);
		uint[] hatcheryDungeonIds = AntonRaidRewardProvider.GetHatcheryDungeonIds();
		foreach (uint hatcheryId in hatcheryDungeonIds)
		{
			for (uint timerType = 1u; timerType <= 3; timerType++)
			{
				CancelTimer(raid.RaidId, timerType, hatcheryId);
			}
			await SetDungeonStateAsync(raid, hatcheryId, 2u);
			await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
			{
				new KeyValuePair<uint, uint>(GetAntonHatcheryFailSymbolId(hatcheryId), 0u),
				new KeyValuePair<uint, uint>(GetAntonHatcheryNamedSymbolId(hatcheryId), 0u)
			});
		}
	}

	private async Task ClearAntonHeartAsync(RaidSnapshot raid, uint clearCount)
	{
		if (clearCount < 5)
		{
			await SetSymbolAsync(raid, GetAntonHpSymbolId(220u), clearCount);
			return;
		}
		await Task.Delay(2000);
		await CompletePhaseTwoAsync(raid);
	}

	private async Task ClearHatcheryAsync(RaidSnapshot raid, uint dungeonId)
	{
		CancelTimer(raid.RaidId, 1u, dungeonId);
		CancelTimer(raid.RaidId, 3u, dungeonId);
		if (!_infectionDungeonByRaid.TryGetValue(raid.RaidId, out var infectionDungeonId) || infectionDungeonId != dungeonId)
		{
			await SetDungeonStateAsync(raid, dungeonId, 3u);
		}
		else
		{
			await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[1]
			{
				new KeyValuePair<uint, uint>(7u, 0u)
			});
			await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(dungeonId, 3u, infectionDungeonId));
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
		if (_raids.TryEnterPhaseBreak(raid.RaidId, out var result))
		{
			CancelTimer(result.RaidId, AttackTimerType, AttackTimerDungeonId);
			CancelAllPhaseTwoTimers(result.RaidId);
			uint[] antonSecondPhaseDungeonIds = AntonSecondPhaseDungeonIds;
			for (int i = 0; i < antonSecondPhaseDungeonIds.Length; i++)
			{
				await SetDungeonStateAsync(dungeonId: antonSecondPhaseDungeonIds[i], raid: result, state: 2u);
			}
			ushort[] eligibleUserIds = (from member in result.Members
				where _raids.HasClearedDungeon(result.RaidId, member.UserId)
				select member.UserId).ToArray();
			_phaseRewardFlows[result.RaidId] = new PhaseRewardFlow(eligibleUserIds);
			await StartPhaseOneResultMovieAsync(result.RaidId);
		}
	}

	private void StartBarrierRecoveryTimer(RaidSnapshot raid)
	{
		int version = AdvanceTimer(raid.RaidId, 4u, 219u);
		RunInBackground(RunBarrierRecoveryTimerAsync(raid, version), "barrier-recovery");
	}

	private async Task RunBarrierRecoveryTimerAsync(RaidSnapshot raid, int version)
	{
		try
		{
			while (true)
			{
				await Task.Delay(1000);
				if (!TimerCurrent(raid.RaidId, 4u, 219u, version) || !TryGetCurrentRaid(raid, out var current) || current.PhaseIndex != 1)
				{
					break;
				}
				uint infectionExists;
				bool infectionActive = _infectionDungeonByRaid.ContainsKey(current.RaidId) && _symbolValues.TryGetValue((current.RaidId, 7u), out infectionExists) && infectionExists != 0;
				await ChangeBlackVolcanoBarrierAsync(operand: AntonRaidRewardProvider.GetShieldChargeRate(infectionActive), raid: current, operation: 1, reason: infectionActive ? "infection-recovery" : "normal-recovery");
				current = null;
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_BARRIER_TIMER failed raid={raid?.RaidId} error={ex2.Message}");
		}
	}

	private async Task ChangeBlackVolcanoBarrierAsync(RaidSnapshot raid, uint operand, byte operation, string reason)
	{
		(uint RaidId, uint AntonBlackVolcanoBarrierSymbolId) key = (RaidId: raid.RaidId, AntonBlackVolcanoBarrierSymbolId: 110u);
		object syncRoot = _raidRuntimeLocks.GetOrAdd(raid.RaidId, (uint _) => new object());
		uint previousValue;
		uint nextValue;
		lock (syncRoot)
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
			_blackVolcanoBarrierBroken[raid.RaidId] = 1;
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
			await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbol(110u, nextValue));
		}
		else
		{
			_blackVolcanoBarrierBroken[raid.RaidId] = 0;
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
		_symbolValues.TryGetValue((raid.RaidId, 110u), out var barrierValue);
		byte broken;
		bool barrierBroken = _blackVolcanoBarrierBroken.TryGetValue(raid.RaidId, out broken) && broken != 0;
		uint activeMovieSymbol = (barrierBroken ? 126u : 127u);
		uint inactiveMovieSymbol = (barrierBroken ? 127u : 126u);
		byte[] statePacket = GamePacketEnvelopeBuilder.Build(0, 584, RaidPacketBuilder.BuildSetSymbols(new KeyValuePair<uint, uint>[4]
		{
			new KeyValuePair<uint, uint>(inactiveMovieSymbol, 0u),
			new KeyValuePair<uint, uint>(110u, barrierValue),
			new KeyValuePair<uint, uint>(111u, barrierBroken ? 1u : 0u),
			new KeyValuePair<uint, uint>(activeMovieSymbol, 1u)
		}));
		await session.SendPacketAsync(statePacket);
		FileLogger.Log($"[GameProtocol] RAID_VOLCANO_SYNC raid={raid.RaidId} barrier={barrierValue} broken={barrierBroken} symbol={activeMovieSymbol}");
	}

	private async Task StartHatcheryOpenTimerAsync(RaidSnapshot raid)
	{
		int version = AdvanceTimer(raid.RaidId, 3u, 219u);
		await SendTimerAsync(raid, 3u, 219u, 180u);
		RunInBackground(RunHatcheryOpenTimerAsync(raid, version), "hatchery-open");
	}

	private async Task RunHatcheryOpenTimerAsync(RaidSnapshot raid, int version)
	{
		try
		{
			await Task.Delay(180000);
			if (TimerCurrent(raid.RaidId, 3u, 219u, version) && TryGetCurrentRaid(raid, out var current) && current.PhaseIndex == 1)
			{
				uint[] hatcheryDungeonIds = AntonRaidRewardProvider.GetHatcheryDungeonIds();
				int omittedIndex = Random.Shared.Next(hatcheryDungeonIds.Length);
				uint[] openHatcheries = SelectAntonOpenHatcheries(omittedIndex);
				uint infectionDungeonId = SelectAntonInfectionHatchery(openHatcheries, Random.Shared.Next(openHatcheries.Length));
				_infectionDungeonByRaid[current.RaidId] = infectionDungeonId;
				_symbolValues[(current.RaidId, 7u)] = 1u;
				_symbolValues[(current.RaidId, 8u)] = infectionDungeonId;
				await SetSymbolAsync(current, 128u, 1u);
				await BroadcastRaidNotificationAsync(current, NotiPacketType.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(openHatcheries.Select((uint id) => new KeyValuePair<uint, uint>(id, 0u)).ToArray(), infectionDungeonId));
				uint[] array = openHatcheries;
				for (int num = 0; num < array.Length; num++)
				{
					await StartHatcheryEffectTimersAsync(dungeonId: array[num], raid: current);
				}
				FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERIES_OPEN raid={current.RaidId} omitted={hatcheryDungeonIds[omittedIndex]} infection={infectionDungeonId}");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERY_OPEN_TIMER failed raid={raid?.RaidId} error={ex2.Message}");
		}
	}

	internal static uint[] SelectAntonOpenHatcheries(int omittedIndex)
	{
		uint[] hatcheryDungeonIds = AntonRaidRewardProvider.GetHatcheryDungeonIds();
		if (omittedIndex < 0 || omittedIndex >= hatcheryDungeonIds.Length)
		{
			throw new ArgumentOutOfRangeException("omittedIndex");
		}
		int openCount = checked((int)AntonRaidRewardProvider.GetHatcheryOpenCount());
		return hatcheryDungeonIds.Where((uint _, int index) => index != omittedIndex).Take(openCount).ToArray();
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
		StartRepeatingHatcherySymbolTimer(raid, dungeonId, 1u, 120u, 45u, GetAntonHatcheryFailSymbolId(dungeonId));
		StartRepeatingHatcherySymbolTimer(raid, dungeonId, 3u, 120u, 50u, GetAntonHatcheryNamedSymbolId(dungeonId));
		return Task.CompletedTask;
	}

	private void StartRepeatingHatcherySymbolTimer(RaidSnapshot raid, uint dungeonId, uint timerType, uint initialSeconds, uint repeatSeconds, uint symbolId)
	{
		int version = AdvanceTimer(raid.RaidId, timerType, dungeonId);
		RunInBackground(SendTimerAsync(raid, timerType, dungeonId, initialSeconds), "hatchery-symbol-send-timer");
		RunInBackground(RunRepeatingHatcherySymbolTimerAsync(raid, dungeonId, timerType, initialSeconds, repeatSeconds, symbolId, version), "hatchery-symbol-repeat");
	}

	private async Task RunRepeatingHatcherySymbolTimerAsync(RaidSnapshot raid, uint dungeonId, uint timerType, uint initialSeconds, uint repeatSeconds, uint symbolId, int version)
	{
		uint delaySeconds = initialSeconds;
		try
		{
			while (true)
			{
				await Task.Delay(checked((int)delaySeconds * 1000));
				if (!TimerCurrent(raid.RaidId, timerType, dungeonId, version) || !TryGetCurrentRaid(raid, out var current) || current.PhaseIndex != 1)
				{
					break;
				}
				await SetSymbolAsync(current, symbolId, 1u);
				await SendTimerAsync(current, timerType, dungeonId, repeatSeconds);
				delaySeconds = repeatSeconds;
				current = null;
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERY_EFFECT_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} type={timerType} error={ex2.Message}");
		}
	}

	private async Task StartHatcheryRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId)
	{
		int version = AdvanceTimer(raid.RaidId, 2u, dungeonId);
		await SendTimerAsync(raid, 2u, dungeonId, 240u);
		RunInBackground(RunHatcheryRecoveryTimerAsync(raid, dungeonId, version), "hatchery-recovery");
	}

	private async Task RunHatcheryRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, int version)
	{
		try
		{
			await Task.Delay(240000);
			if (TimerCurrent(raid.RaidId, 2u, dungeonId, version) && TryGetCurrentRaid(raid, out var current) && current.PhaseIndex == 1)
			{
				_raids.ResetClearCounts(current.RaidId, new uint[1] { dungeonId });
				await SetSymbolAsync(current, GetAntonHpSymbolId(dungeonId), 0u);
				if (!_infectionDungeonByRaid.TryGetValue(current.RaidId, out var infectionDungeonId) || infectionDungeonId != dungeonId)
				{
					await SetDungeonStateAsync(current, dungeonId, 0u);
				}
				else
				{
					await SetSymbolAsync(current, 7u, 1u);
					await BroadcastRaidNotificationAsync(current, NotiPacketType.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(dungeonId, 0u, infectionDungeonId));
				}
				await StartHatcheryEffectTimersAsync(current, dungeonId);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERY_RECOVERY_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} error={ex2.Message}");
		}
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
		if (_raids.TryEnterPhaseBreak(raid.RaidId, out var waiting))
		{
			CancelTimer(waiting.RaidId, AttackTimerType, AttackTimerDungeonId);
			CancelAllPhaseOneTimers(raid.RaidId);
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
			_phaseRewardFlows[waiting.RaidId] = new PhaseRewardFlow(eligibleUserIds);
			await Task.Delay(2000);
			await SetSymbolAsync(waiting, 105u, 1u);
			await StartPhaseOneResultMovieAsync(waiting.RaidId);
		}
	}
}
