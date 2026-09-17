using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	private async Task StartRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, uint recovery, uint active, Func<RaidSnapshot, Task> timeout)
	{
		Guid version = AdvanceTimer(raid.RaidId, 2u, dungeonId);
		await SendTimerAsync(raid, 2u, dungeonId, recovery);
		RunInBackground(RunRecoveryTimerAsync(raid, dungeonId, recovery, active, version, timeout), "dungeon-recovery");
	}

	private async Task RunRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, uint recovery, uint active, Guid version, Func<RaidSnapshot, Task> timeout)
	{
		_ = 6;
		try
		{
			await Task.Delay((int)(recovery * 1000));
			if (TimerCurrent(raid.RaidId, 2u, dungeonId, version) && TryGetCurrentRaid(raid, out var current))
			{
				await SetDungeonStateAsync(current, dungeonId, 0u);
				_raids.ResetClearCounts(current.RaidId, new uint[1] { dungeonId });
				await SetSymbolAsync(current, GetAntonHpSymbolId(dungeonId), 0u);
				switch (dungeonId)
				{
				case 211u:
					await StartBlackFogPassiveTimerAsync(current);
					break;
				case 216u:
					await SetSymbolAsync(current, 123u, 1u);
					await StartNavalCannonMeteoTimerAsync(current);
					break;
				}
				await StartActiveTimerAsync(current, dungeonId, active, timeout);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_RECOVERY_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} error={ex.Message}");
		}
	}

	private async Task StartBlackFogPassiveTimerAsync(RaidSnapshot raid)
	{
		Guid version = AdvanceTimer(raid.RaidId, 3u, 211u);
		await SendTimerAsync(raid, 3u, 211u, 240u);
		RunInBackground(RunBlackFogPassiveTimerAsync(raid, version), "black-fog-passive");
	}

	private async Task RunBlackFogPassiveTimerAsync(RaidSnapshot raid, Guid version)
	{
		_ = 1;
		try
		{
			await Task.Delay(240000);
			if (TimerCurrent(raid.RaidId, 3u, 211u, version) && TryGetCurrentRaid(raid, out var current))
			{
				await SetSymbolAsync(current, 1u, 1u);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_BLACK_FOG_PASSIVE_TIMER failed raid={raid?.RaidId} error={ex.Message}");
		}
	}

	private async Task StartNavalCannonMeteoTimerAsync(RaidSnapshot raid)
	{
		Guid version = AdvanceTimer(raid.RaidId, 3u, 216u);
		await SendTimerAsync(raid, 3u, 216u, 120u);
		RunInBackground(RunNavalCannonMeteoTimerAsync(raid, version), "naval-cannon-meteo");
	}

	private async Task RunNavalCannonMeteoTimerAsync(RaidSnapshot raid, Guid version)
	{
		_ = 1;
		try
		{
			await Task.Delay(120000);
			if (TimerCurrent(raid.RaidId, 3u, 216u, version) && TryGetCurrentRaid(raid, out var current))
			{
				await SetSymbolAsync(current, 2u, 1u);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_NAVAL_METEO_TIMER failed raid={raid?.RaidId} error={ex.Message}");
		}
	}

	private async Task StartActiveTimerAsync(RaidSnapshot raid, uint dungeonId, uint seconds, Func<RaidSnapshot, Task> timeout)
	{
		Guid version = AdvanceTimer(raid.RaidId, 1u, dungeonId);
		await SendTimerAsync(raid, 1u, dungeonId, seconds);
		RunInBackground(RunActiveTimerAsync(raid, dungeonId, seconds, version, timeout), "dungeon-active");
	}

	private async Task RunActiveTimerAsync(RaidSnapshot raid, uint dungeonId, uint seconds, Guid version, Func<RaidSnapshot, Task> timeout)
	{
		_ = 1;
		try
		{
			await Task.Delay((int)(seconds * 1000));
			if (TimerCurrent(raid.RaidId, 1u, dungeonId, version) && TryGetCurrentRaid(raid, out var current))
			{
				await timeout(current);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log($"[GameProtocol] RAID_ACTIVE_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} error={ex.Message}");
		}
	}

	private Task SendTimerAsync(RaidSnapshot raid, uint type, uint dungeonId, uint seconds)
	{
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(type, dungeonId, seconds));
	}

	private Task SetDungeonStateAsync(RaidSnapshot raid, uint dungeonId, uint state)
	{
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_CHANGE_DUNGEON_STATE, RaidPacketBuilder.BuildChangeDungeonState(dungeonId, state));
	}

	private Task SetSymbolAsync(RaidSnapshot raid, uint symbolId, uint value)
	{
		_symbolValues[(raid.RaidId, symbolId)] = value;
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbol(symbolId, value));
	}

	private Task SetSymbolsAsync(RaidSnapshot raid, IReadOnlyList<KeyValuePair<uint, uint>> values)
	{
		foreach (KeyValuePair<uint, uint> value in values)
		{
			_symbolValues[(raid.RaidId, value.Key)] = value.Value;
		}
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(values));
	}

	private bool TryGetCurrentRaid(RaidSnapshot raid, out RaidSnapshot current)
	{
		current = null;
		if (raid != null && _raids.TryGetByRaidId(raid.RaidId, out current) && current.InstanceId == raid.InstanceId && current.State == 2)
		{
			return current.PhaseIndex == raid.PhaseIndex;
		}
		return false;
	}

	private void StartAttackTimeoutTimer(RaidSnapshot raid, uint remainingSeconds)
	{
		Guid version = AdvanceTimer(raid.RaidId, 0u, 0u);
		ClockService.Instance.ScheduleOneShotAfterAsync($"raid-attack:{raid.InstanceId}", TimeSpan.FromSeconds(remainingSeconds), (DateTime _) => RunAttackTimeoutAsync(raid, version));
	}

	private async Task RunAttackTimeoutAsync(RaidSnapshot expected, Guid version)
	{
		uint raidId = expected.RaidId;
		uint phaseIndex = expected.PhaseIndex;
		RaidSnapshot disbanded = null;
		try
		{
			_ = 4;
			try
			{
				if (!TimerCurrent(raidId, 0u, 0u, version) || !_raids.TryFailAndDisband(expected, out var failed))
				{
					goto end_IL_0070;
				}
				disbanded = failed;
				if (phaseIndex == 0)
				{
					CancelAllPhaseOneTimers(raidId);
				}
				else
				{
					CancelAllPhaseTwoTimers(raidId);
				}
				uint[] array = ((phaseIndex == 0) ? AntonFirstPhaseDungeonIds : AntonSecondPhaseDungeonIds);
				uint[] array2 = array;
				foreach (uint dungeonId in array2)
				{
					await SetDungeonStateAsync(failed, dungeonId, 2u);
				}
				if (phaseIndex == 0)
				{
					await SetSymbolAsync(failed, 104u, 1u);
				}
				await BroadcastRaidNotificationAsync(failed, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(0, 0u));
				foreach (RaidMember member in failed.Members)
				{
					if (_sessions.TryGet(checked((int)member.CharacterId), out var session) && session.SessionId == member.SessionId)
					{
						await SessionDirectory.TrySendBestEffortAsync((CancellationToken cancellationToken) => session.SendPacketAsync(BuildFailedRaidResultPacket(failed), cancellationToken), $"raid timeout raid={raidId} recipient={session.SessionId}");
					}
				}
				await EnablePhaseOneDungeonReturnAsync(failed);
				FileLogger.Log($"[GameProtocol] RAID_ATTACK_TIMEOUT_DISBANDED raid={raidId} phase={phaseIndex} elapsed={failed.PhaseClearTimeSeconds} deaths={failed.PhaseDeathCount} members={failed.Members.Count}");
				goto end_IL_0051;
				end_IL_0070:;
			}
			catch (Exception ex)
			{
				FileLogger.Log($"[GameProtocol] RAID_ATTACK_TIMEOUT failed raid={raidId} phase={phaseIndex} error={ex.Message}");
				goto end_IL_0051;
			}
			end_IL_0051:;
		}
		finally
		{
			if (disbanded != null)
			{
				ClearDisbandedRaidState(disbanded);
				await BroadcastRaidDepartureAsync(disbanded);
			}
		}
	}

	internal Guid AdvanceTimer(uint raidId, uint type, uint dungeonId)
	{
		return _timerVersions.AddOrUpdate(TimerKey(raidId, type, dungeonId), Guid.NewGuid(), (string _, Guid _) => Guid.NewGuid());
	}

	private void CancelTimer(uint raidId, uint type, uint dungeonId)
	{
		AdvanceTimer(raidId, type, dungeonId);
	}

	internal bool TimerCurrent(uint raidId, uint type, uint dungeonId, Guid version)
	{
		if (_timerVersions.TryGetValue(TimerKey(raidId, type, dungeonId), out var value))
		{
			return value == version;
		}
		return false;
	}

	private void CancelAllPhaseOneTimers(uint raidId)
	{
		uint[] antonFirstPhaseDungeonIds = AntonFirstPhaseDungeonIds;
		foreach (uint dungeonId in antonFirstPhaseDungeonIds)
		{
			for (uint num = 1u; num <= 3; num++)
			{
				CancelTimer(raidId, num, dungeonId);
			}
		}
	}

	private void CancelAllPhaseTwoTimers(uint raidId)
	{
		uint[] antonSecondPhaseDungeonIds = AntonSecondPhaseDungeonIds;
		foreach (uint dungeonId in antonSecondPhaseDungeonIds)
		{
			for (uint num = 1u; num <= 3; num++)
			{
				CancelTimer(raidId, num, dungeonId);
			}
		}
		CancelTimer(raidId, 4u, 219u);
	}

	internal void CleanupRaidRuntimeState(uint raidId)
	{
		_phaseRewardFlows.TryRemove(raidId, out var _);
		_infectionDungeonByRaid.TryRemove(raidId, out var value2);
		_blackVolcanoBarrierBroken.TryRemove(raidId, out var _);
		_raidRuntimeLocks.TryRemove(raidId, out var _);
		foreach (var key in _raidBuffActivations.Keys)
		{
			if (key.RaidId == raidId)
			{
				_raidBuffActivations.TryRemove(key, out var _);
			}
		}
		foreach (var key2 in _raidMonsterRuntimeValues.Keys)
		{
			if (key2.RaidId == raidId)
			{
				_raidMonsterRuntimeValues.TryRemove(key2, out var _);
			}
		}
		foreach (var key3 in _symbolValues.Keys)
		{
			if (key3.RaidId == raidId)
			{
				_symbolValues.TryRemove(key3, out value2);
			}
		}
		string value7 = raidId + ":";
		foreach (string key4 in _timerVersions.Keys)
		{
			if (key4.StartsWith(value7, StringComparison.Ordinal))
			{
				_timerVersions.TryRemove(key4, out var _);
			}
		}
	}

	private static string TimerKey(uint raidId, uint type, uint dungeonId)
	{
		return raidId + ":" + type + ":" + dungeonId;
	}

	internal static bool IsAntonRaidDungeon(int dungeonId)
	{
		if (!IsAntonFirstPhaseDungeon(dungeonId))
		{
			if (dungeonId >= 218)
			{
				return dungeonId <= 224;
			}
			return false;
		}
		return true;
	}

	internal static bool IsAntonFirstPhaseDungeon(int dungeonId)
	{
		if (dungeonId >= 210)
		{
			return dungeonId <= 216;
		}
		return false;
	}

	internal static bool IsAntonDungeonForPhase(uint phaseIndex, int dungeonId)
	{
		if (phaseIndex != 0)
		{
			if (phaseIndex == 1 && dungeonId >= 218)
			{
				return dungeonId <= 224;
			}
			return false;
		}
		return IsAntonFirstPhaseDungeon(dungeonId);
	}

	private static uint GetAntonRequiredClears(uint dungeonId)
	{
		switch (dungeonId)
		{
		case 210u:
			return 4u;
		case 220u:
			return 5u;
		default:
			return 1u;
		case 213u:
		case 215u:
			return 2u;
		}
	}

	internal static uint GetAntonHpSymbolId(uint dungeonId)
	{
		if (dungeonId > 216)
		{
			return dungeonId - 161;
		}
		return dungeonId - 160;
	}

	private Task SendRaidClearCountsAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		uint[] obj = ((raid.PhaseIndex == 0) ? new uint[3] { 210u, 213u, 215u } : new uint[1] { 220u });
		List<KeyValuePair<uint, uint>> list = new List<KeyValuePair<uint, uint>>();
		uint[] array = obj;
		foreach (uint dungeonId in array)
		{
			_raids.TryGetClearCount(raid.RaidId, dungeonId, out var clearCount);
			list.Add(new KeyValuePair<uint, uint>(GetAntonHpSymbolId(dungeonId), clearCount));
		}
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(list)));
	}
}
