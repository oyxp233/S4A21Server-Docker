using System;
using System.Collections.Generic;
using System.Linq;
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
	private sealed class RaidTimerRegistration
	{
		internal Guid RaidInstanceId { get; init; }
		internal uint PhaseIndex { get; init; }
		internal uint TimerType { get; init; }
		internal uint DungeonId { get; init; }
		internal string Purpose { get; init; } = string.Empty;
		internal Guid Version { get; init; }
		internal DateTime DeadlineUtc { get; init; }
		internal bool ProjectSetTimer { get; init; }
		internal byte? RemainTimeState { get; init; }
		internal ClockService.ClockTimerHandle Handle { get; set; }
	}

	private async Task StartRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, uint recovery, uint active, Func<RaidSnapshot, Task> timeout)
	{
		ScheduleRaidTimer(
			raid,
			2u,
			dungeonId,
			"recovery",
			recovery,
			projectSetTimer: true,
			remainTimeState: null,
			async (current, _) =>
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
					await SetSymbolAsync(current, AntonNavigunOnMovieSymbolId, 1u);
					await StartNavalCannonMeteoTimerAsync(current);
					break;
				}
				await StartActiveTimerAsync(current, dungeonId, active, timeout);
			});
		await SendTimerAsync(raid, 2u, dungeonId, recovery);
	}

	private async Task StartBlackFogPassiveTimerAsync(RaidSnapshot raid)
	{
		uint seconds = _timerConfiguration.GetDungeonPassiveSeconds(0u, 211u);
		ScheduleRaidTimer(
			raid,
			3u,
			211u,
			"passive",
			seconds,
			projectSetTimer: true,
			remainTimeState: null,
			(current, _) => SetSymbolAsync(current, AntonSmokePassiveCreateSymbolId, 1u));
		await SendTimerAsync(raid, 3u, 211u, seconds);
	}

	private async Task StartNavalCannonMeteoTimerAsync(RaidSnapshot raid)
	{
		uint seconds = _timerConfiguration.GetDungeonPassiveSeconds(0u, 216u);
		ScheduleRaidTimer(
			raid,
			3u,
			216u,
			"passive",
			seconds,
			projectSetTimer: true,
			remainTimeState: null,
			(current, _) => SetSymbolAsync(current, AntonMeteoPassiveCreateSymbolId, 1u));
		await SendTimerAsync(raid, 3u, 216u, seconds);
	}

	private async Task StartActiveTimerAsync(RaidSnapshot raid, uint dungeonId, uint seconds, Func<RaidSnapshot, Task> timeout)
	{
		ScheduleRaidTimer(
			raid,
			1u,
			dungeonId,
			"active",
			seconds,
			projectSetTimer: true,
			remainTimeState: null,
			(current, _) => timeout(current));
		await SendTimerAsync(raid, 1u, dungeonId, seconds);
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
		_symbolValues[(raid.InstanceId, symbolId)] = value;
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbol(symbolId, value));
	}

	private Task SetSymbolsAsync(RaidSnapshot raid, IReadOnlyList<KeyValuePair<uint, uint>> values)
	{
		foreach (KeyValuePair<uint, uint> value in values)
		{
			_symbolValues[(raid.InstanceId, value.Key)] = value.Value;
		}
		return BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(values));
	}

	private bool TryGetCurrentRaid(RaidSnapshot raid, out RaidSnapshot current)
	{
		current = null;
		if (raid != null
			&& _raids.TryGetByRaidId(raid.RaidId, out current)
			&& current.InstanceId == raid.InstanceId
			&& current.State == raid.State)
		{
			return current.PhaseIndex == raid.PhaseIndex;
		}
		return false;
	}

	private bool TryGetPhaseRewardFlow(RaidSnapshot raid, out PhaseRewardFlow flow)
	{
		flow = null;
		return raid != null
			&& _phaseRewardFlows.TryGetValue(raid.InstanceId, out flow);
	}

	private void StartAttackTimeoutTimer(RaidSnapshot raid, uint remainingSeconds)
	{
		ScheduleRaidTimer(
			raid,
			AttackTimerType,
			AttackTimerDungeonId,
			"attack",
			remainingSeconds,
			projectSetTimer: true,
			remainTimeState: 0,
			(current, version) => RunAttackTimeoutAsync(current, version));
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
				if (!TimerCurrent(expected, AttackTimerType, AttackTimerDungeonId, "attack", version)
					|| !_raids.TryFailAndDisband(expected, out var failed))
				{
					goto end_IL_0070;
				}
				disbanded = failed;
				if (phaseIndex == 0)
				{
					CancelAllPhaseOneTimers(failed);
				}
				else
				{
					CancelAllPhaseTwoTimers(failed);
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

	internal Guid ScheduleRaidTimer(
		RaidSnapshot expected,
		uint timerType,
		uint dungeonId,
		string purpose,
		uint seconds,
		bool projectSetTimer,
		byte? remainTimeState,
		Func<RaidSnapshot, Guid, Task> callback,
		Guid? retainedVersion = null)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(callback);
		if (seconds == 0)
		{
			FileLogger.Log(
				$"[GameProtocol] RAID_TIMER rejected zero duration " +
				$"raid={expected.RaidId} instance={expected.InstanceId} " +
				$"phase={expected.PhaseIndex} type={timerType} dungeon={dungeonId} purpose={purpose}");
			return Guid.Empty;
		}
		string key = TimerKey(expected, timerType, dungeonId, purpose);
		lock (_timerRegistrationLock)
		{
			if (retainedVersion.HasValue
				&& (!_timerRegistrations.TryGetValue(key, out var continuing)
					|| continuing.Version != retainedVersion.Value))
			{
				return Guid.Empty;
			}

			Guid version = retainedVersion ?? Guid.NewGuid();
			DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(seconds);
			var registration = new RaidTimerRegistration
			{
				RaidInstanceId = expected.InstanceId,
				PhaseIndex = expected.PhaseIndex,
				TimerType = timerType,
				DungeonId = dungeonId,
				Purpose = purpose,
				Version = version,
				DeadlineUtc = deadlineUtc,
				ProjectSetTimer = projectSetTimer,
				RemainTimeState = remainTimeState,
			};
			registration.Handle = _clock.ScheduleOneShotAsync(key, deadlineUtc, async _ =>
			{
				try
				{
					if (TimerCurrent(expected, key, version) && TryGetCurrentRaid(expected, out var current))
						await callback(current, version);
				}
				finally
				{
					RemoveRegistrationIfCurrent(key, registration);
				}
			});

			if (_timerRegistrations.TryGetValue(key, out var previous))
				previous.Handle?.Cancel();
			_timerRegistrations[key] = registration;
			return version;
		}
	}

	private void RemoveRegistrationIfCurrent(string key, RaidTimerRegistration expected)
	{
		lock (_timerRegistrationLock)
		{
			if (_timerRegistrations.TryGetValue(key, out var current) && ReferenceEquals(current, expected))
				_timerRegistrations.Remove(key);
		}
	}

	internal void CancelTimer(RaidSnapshot raid, uint type, uint dungeonId)
	{
		if (raid == null)
			return;
		lock (_timerRegistrationLock)
		{
			string[] keys = _timerRegistrations
				.Where(entry => entry.Value.RaidInstanceId == raid.InstanceId
					&& entry.Value.TimerType == type
					&& entry.Value.DungeonId == dungeonId)
				.Select(entry => entry.Key)
				.ToArray();
			foreach (string key in keys)
			{
				RaidTimerRegistration registration = _timerRegistrations[key];
				_timerRegistrations.Remove(key);
				registration.Handle?.Cancel();
			}
		}
	}

	internal bool TimerCurrent(
		RaidSnapshot raid,
		uint type,
		uint dungeonId,
		string purpose,
		Guid version)
	{
		return raid != null && TimerCurrent(raid, TimerKey(raid, type, dungeonId, purpose), version);
	}

	private bool TimerCurrent(RaidSnapshot raid, string key, Guid version)
	{
		lock (_timerRegistrationLock)
		{
			return _timerRegistrations.TryGetValue(key, out var current)
				&& current.RaidInstanceId == raid.InstanceId
				&& current.PhaseIndex == raid.PhaseIndex
				&& current.Version == version;
		}
	}

	private void CancelAllPhaseOneTimers(RaidSnapshot raid)
	{
		CancelPhaseTimers(raid);
	}

	private void CancelAllPhaseTwoTimers(RaidSnapshot raid)
	{
		CancelPhaseTimers(raid);
	}

	private void CancelPhaseTimers(RaidSnapshot raid)
	{
		if (raid == null)
			return;
		lock (_timerRegistrationLock)
		{
			string[] keys = _timerRegistrations
				.Where(entry => entry.Value.RaidInstanceId == raid.InstanceId
					&& entry.Value.PhaseIndex == raid.PhaseIndex)
				.Select(entry => entry.Key)
				.ToArray();
			foreach (string key in keys)
			{
				RaidTimerRegistration registration = _timerRegistrations[key];
				_timerRegistrations.Remove(key);
				registration.Handle?.Cancel();
			}
		}
	}

	internal void CleanupRaidRuntimeState(RaidSnapshot raid)
	{
		if (raid == null)
			return;
		_clock.CancelOneShotsByPrefix($"raid:{raid.InstanceId:N}:");
		lock (_timerRegistrationLock)
		{
			foreach (string key in _timerRegistrations
				.Where(entry => entry.Value.RaidInstanceId == raid.InstanceId)
				.Select(entry => entry.Key)
				.ToArray())
			{
				_timerRegistrations.Remove(key);
			}
		}
		_phaseRewardFlows.TryRemove(raid.InstanceId, out var _);
		_infectionDungeonByRaid.TryRemove(raid.InstanceId, out var _);
		_blackVolcanoBarrierBroken.TryRemove(raid.InstanceId, out var _);
		_raidRuntimeLocks.TryRemove(raid.InstanceId, out var _);
		foreach (var key in _raidBuffActivations.Keys)
		{
			if (key.RaidInstanceId == raid.InstanceId)
			{
				_raidBuffActivations.TryRemove(key, out var _);
			}
		}
		foreach (var key2 in _raidMonsterRuntimeValues.Keys)
		{
			if (key2.RaidInstanceId == raid.InstanceId)
			{
				_raidMonsterRuntimeValues.TryRemove(key2, out var _);
			}
		}
		foreach (var key3 in _symbolValues.Keys)
		{
			if (key3.RaidInstanceId == raid.InstanceId)
			{
				_symbolValues.TryRemove(key3, out var _);
			}
		}
	}

	private static string TimerKey(RaidSnapshot raid, uint type, uint dungeonId, string purpose)
	{
		return $"raid:{raid.InstanceId:N}:timer:{type}:{dungeonId}:{purpose}";
	}

	private async Task SendRaidTimerSnapshotAsync(
		EnhancedClientSession session,
		RaidSnapshot raid,
		DateTime utcNow)
	{
		foreach (byte[] packet in BuildRaidTimerSnapshotPackets(raid, utcNow))
			await session.SendPacketAsync(packet);
	}

	internal IReadOnlyList<byte[]> BuildRaidTimerSnapshotPackets(RaidSnapshot raid, DateTime utcNow)
	{
		RaidTimerRegistration[] registrations;
		lock (_timerRegistrationLock)
		{
			registrations = _timerRegistrations.Values
				.Where(entry => entry.RaidInstanceId == raid.InstanceId && entry.PhaseIndex == raid.PhaseIndex)
				.OrderBy(entry => entry.DeadlineUtc)
				.ThenBy(entry => entry.TimerType)
				.ThenBy(entry => entry.DungeonId)
				.ToArray();
		}

		if (utcNow.Kind == DateTimeKind.Local)
			utcNow = utcNow.ToUniversalTime();
		else if (utcNow.Kind == DateTimeKind.Unspecified)
			utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
		var packets = new List<byte[]>();
		foreach (RaidTimerRegistration registration in registrations)
		{
			double remaining = (registration.DeadlineUtc - utcNow).TotalSeconds;
			uint remainingSeconds = remaining <= 0
				? 0u
				: checked((uint)Math.Ceiling(remaining));
			if (registration.ProjectSetTimer)
			{
				packets.Add(GamePacketEnvelopeBuilder.Build(
					0,
					(ushort)NotiPacketTypeA21.RAID_SET_TIMER,
					RaidPacketBuilder.BuildSetTimer(
						registration.TimerType,
						registration.DungeonId,
						remainingSeconds)));
			}
			if (registration.RemainTimeState.HasValue)
			{
				packets.Add(GamePacketEnvelopeBuilder.Build(
					0,
					(ushort)NotiPacketTypeA21.RAID_REMAIN_TIME,
					RaidPacketBuilder.BuildRemainTime(
						registration.RemainTimeState.Value,
						remainingSeconds)));
			}
		}
		return packets;
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
