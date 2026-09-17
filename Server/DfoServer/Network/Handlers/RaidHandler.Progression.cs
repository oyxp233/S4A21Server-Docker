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
		await BroadcastRaidNotificationAsync(raid3, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, 3u));
		await BroadcastRaidNotificationAsync(raid3, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, 3u));
		await BroadcastRaidNotificationAsync(raid3, NotiPacketTypeA21.PREPARE_START_RAID, Array.Empty<byte>());
		ClockService.Instance.ScheduleOneShotAfterAsync($"raid-preparation:{raid3.InstanceId}:{raid3.PreparationGeneration}", TimeSpan.FromSeconds(3L), (DateTime _) => CompleteRaidPreparationAsync(raid3));
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
		CancelTimer(prepared.RaidId, 0u, 1u);
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
		ClockService.Instance.ScheduleOneShotAfterAsync($"raid-phase2-preparation:{prepared.InstanceId}:{prepared.PreparationGeneration}", TimeSpan.FromSeconds(readySeconds), (DateTime _) => CompletePhaseTwoPreparationAsync(prepared, reason));
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
			await StartActiveTimerAsync(raid, 212u, 300u, ResetPhaseOneAsync);
			await StartActiveTimerAsync(raid, 214u, 300u, ResetPhaseOneAsync);
			await StartRecoveryTimerAsync(raid, 211u, 300u, 480u, ResetPhaseOneAsync);
			RunInBackground(OpenNavalCannonAfterDelayAsync(raid), "open-naval-cannon");
		}
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
		_ = 4;
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
			FileLogger.Log($"[GameProtocol] RAID_NAVAL_OPEN failed raid={raid?.RaidId} error={ex.Message}");
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
		CancelTimer(raid.RaidId, 3u, 219u);
		CancelTimer(raid.RaidId, 4u, 219u);
		_infectionDungeonByRaid.TryRemove(raid.RaidId, out var _);
		await SetSymbolsAsync(raid, new KeyValuePair<uint, uint>[2]
		{
			new KeyValuePair<uint, uint>(7u, 0u),
			new KeyValuePair<uint, uint>(8u, 0u)
		});
		uint[] array = new uint[2] { 218u, 219u };
		uint[] array2 = array;
		foreach (uint dungeonId in array2)
		{
			await SetDungeonStateAsync(raid, dungeonId, 2u);
		}
		await SetDungeonStateAsync(raid, 220u, 0u);
		array = AntonRaidRewardProvider.GetHatcheryDungeonIds();
		array2 = array;
		foreach (uint hatcheryId in array2)
		{
			for (uint num = 1u; num <= 3; num++)
			{
				CancelTimer(raid.RaidId, num, hatcheryId);
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
		await SetSymbolAsync(raid, GetAntonHpSymbolId(220u), clearCount);
		if (clearCount >= 5)
		{
			await Task.Delay(2000);
			await CompletePhaseTwoAsync(raid);
		}
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
			CancelTimer(result.RaidId, 0u, 0u);
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
		Guid version = AdvanceTimer(raid.RaidId, 4u, 219u);
		RunInBackground(RunBarrierRecoveryTimerAsync(raid, version), "barrier-recovery");
	}

	private async Task RunBarrierRecoveryTimerAsync(RaidSnapshot raid, Guid version)
	{
		_ = 1;
		try
		{
			while (true)
			{
				await Task.Delay(1000);
				if (TimerCurrent(raid.RaidId, 4u, 219u, version) && TryGetCurrentRaid(raid, out var current) && current.PhaseIndex == 1)
				{
					bool flag = _infectionDungeonByRaid.ContainsKey(current.RaidId) && _symbolValues.TryGetValue((current.RaidId, 7u), out var value) && value != 0;
					await ChangeBlackVolcanoBarrierAsync(operand: AntonRaidRewardProvider.GetShieldChargeRate(flag), raid: current, operation: 1, reason: flag ? "infection-recovery" : "normal-recovery");
					current = null;
					continue;
				}
				break;
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_BARRIER_TIMER failed raid={raid?.RaidId} error={ex.Message}");
		}
	}

	private async Task ChangeBlackVolcanoBarrierAsync(RaidSnapshot raid, uint operand, byte operation, string reason)
	{
		(uint, uint) key = (raid.RaidId, 110u);
		uint previousValue;
		uint nextValue;
		lock (_raidRuntimeLocks.GetOrAdd(raid.RaidId, (uint _) => new object()))
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
			await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbol(110u, nextValue));
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
		bool barrierBroken = _blackVolcanoBarrierBroken.TryGetValue(raid.RaidId, out var value) && value != 0;
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
		Guid version = AdvanceTimer(raid.RaidId, 3u, 219u);
		await SendTimerAsync(raid, 3u, 219u, 180u);
		RunInBackground(RunHatcheryOpenTimerAsync(raid, version), "hatchery-open");
	}

	private async Task RunHatcheryOpenTimerAsync(RaidSnapshot raid, Guid version)
	{
		_ = 3;
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
				await BroadcastRaidNotificationAsync(current, NotiPacketTypeA21.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(openHatcheries.Select((uint id) => new KeyValuePair<uint, uint>(id, 0u)).ToArray(), infectionDungeonId));
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
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERY_OPEN_TIMER failed raid={raid?.RaidId} error={ex.Message}");
		}
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
		StartRepeatingHatcherySymbolTimer(raid, dungeonId, 1u, 120u, 45u, GetAntonHatcheryFailSymbolId(dungeonId));
		StartRepeatingHatcherySymbolTimer(raid, dungeonId, 3u, 120u, 50u, GetAntonHatcheryNamedSymbolId(dungeonId));
		return Task.CompletedTask;
	}

	private void StartRepeatingHatcherySymbolTimer(RaidSnapshot raid, uint dungeonId, uint timerType, uint initialSeconds, uint repeatSeconds, uint symbolId)
	{
		Guid version = AdvanceTimer(raid.RaidId, timerType, dungeonId);
		RunInBackground(SendTimerAsync(raid, timerType, dungeonId, initialSeconds), "hatchery-symbol-send-timer");
		RunInBackground(RunRepeatingHatcherySymbolTimerAsync(raid, dungeonId, timerType, initialSeconds, repeatSeconds, symbolId, version), "hatchery-symbol-repeat");
	}

	private async Task RunRepeatingHatcherySymbolTimerAsync(RaidSnapshot raid, uint dungeonId, uint timerType, uint initialSeconds, uint repeatSeconds, uint symbolId, Guid version)
	{
		uint num = initialSeconds;
		try
		{
			while (true)
			{
				await Task.Delay(checked((int)num * 1000));
				if (!TimerCurrent(raid.RaidId, timerType, dungeonId, version) || !TryGetCurrentRaid(raid, out var current) || current.PhaseIndex != 1)
				{
					break;
				}
				await SetSymbolAsync(current, symbolId, 1u);
				await SendTimerAsync(current, timerType, dungeonId, repeatSeconds);
				num = repeatSeconds;
				current = null;
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERY_EFFECT_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} type={timerType} error={ex.Message}");
		}
	}

	private async Task StartHatcheryRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId)
	{
		Guid version = AdvanceTimer(raid.RaidId, 2u, dungeonId);
		await SendTimerAsync(raid, 2u, dungeonId, 240u);
		RunInBackground(RunHatcheryRecoveryTimerAsync(raid, dungeonId, version), "hatchery-recovery");
	}

	private async Task RunHatcheryRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, Guid version)
	{
		_ = 5;
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
					await BroadcastRaidNotificationAsync(current, NotiPacketTypeA21.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(dungeonId, 0u, infectionDungeonId));
				}
				await StartHatcheryEffectTimersAsync(current, dungeonId);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_PHASE2_HATCHERY_RECOVERY_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} error={ex.Message}");
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
		if (_raids.TryEnterPhaseBreak(raid, out var waiting))
		{
			CancelTimer(waiting.RaidId, 0u, 0u);
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
		await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, 2400u));
		await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, 2400u));
		StartAttackTimeoutTimer(started, 2400u);
		FileLogger.Log($"[GameProtocol] START_RAID_ATTACK raid={started.RaidId} state={started.State} dungeon={210u} seconds={2400u}");
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
				_blackVolcanoBarrierBroken[started.RaidId] = 0;
				await SetSymbolsAsync(started, AntonSecondPhaseInitialSymbols);
				await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_DUNGEON_STATE, RaidPacketBuilder.BuildDungeonState(AntonSecondPhaseInitialDungeonStates));
				await BroadcastRaidSituationAsync(started);
				await PulseSymbolAsync(started, 127u);
				await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, 2400u));
				await BroadcastRaidNotificationAsync(started, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, 2400u));
				StartAttackTimeoutTimer(started, 2400u);
				await StartHatcheryOpenTimerAsync(started);
				StartBarrierRecoveryTimer(started);
				FileLogger.Log($"[GameProtocol] START_RAID_PHASE2_ATTACK raid={started.RaidId} reason={reason} seconds={2400u}");
			}
		}
	}

	private void SchedulePhaseBreakTimer(RaidSnapshot expected, uint seconds)
	{
		uint raidId = expected.RaidId;
		Guid version = AdvanceTimer(raidId, 0u, 1u);
		ClockService.Instance.ScheduleOneShotAfterAsync($"raid-phase-break:{expected.InstanceId}", TimeSpan.FromSeconds(seconds), async delegate
		{
			if (TimerCurrent(raidId, 0u, 1u, version) && _raids.TryPrepareNextPhaseAutomatically(expected, out var raid, (IReadOnlyList<RaidMember> members) => PreparationPartyOrder?.Invoke(members)))
			{
				await PrepareAndStartAntonPhaseTwoAsync(raid, "phase-break-timeout");
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
