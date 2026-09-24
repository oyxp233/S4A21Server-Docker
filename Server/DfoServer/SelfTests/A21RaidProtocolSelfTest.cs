using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Builders.Raid;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Party;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests;

public static class A21RaidProtocolSelfTest
{
	public class UnusedDependency : DispatchProxy
	{
		protected override object Invoke(MethodInfo method, object[] args)
		{
			throw new InvalidOperationException("Unexpected dependency call: " + method.Name);
		}
	}

	private sealed class RaidWireClient : IDisposable
	{
		public TcpClient Reader { get; } = new TcpClient();

		public EnhancedClientSession Session { get; }

		public RaidMember Member => new RaidMember
		{
			UserId = Session.Player.UserId,
			CharacterId = (uint)Session.Player.CharacterId,
			SessionId = Session.SessionId,
			PartyIndex = 1,
			NameBytes = new byte[1] { 65 }
		};

		public RaidWireClient(ushort userId, int port = 10200)
		{
			using TcpListener tcpListener = new TcpListener(IPAddress.Loopback, 0);
			tcpListener.Start();
			Reader.Connect((IPEndPoint)tcpListener.LocalEndpoint);
			Session = new EnhancedClientSession(tcpListener.AcceptTcpClient(), default(GamePacketHeader), port);
			Session.Player.UserId = userId;
			Session.Player.CharacterId = userId;
		}

		public List<byte[]> ReadThroughDirectory()
		{
			using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5L));
			List<byte[]> list = new List<byte[]>();
			for (int i = 0; i < 100; i++)
			{
				byte[] array = new byte[15];
				Reader.GetStream().ReadExactlyAsync(array, cancellationTokenSource.Token).AsTask()
					.GetAwaiter()
					.GetResult();
				int num = checked((int)BitConverter.ToUInt32(array, 3));
				if (num < 15 || num > 1048576)
				{
					throw new InvalidOperationException("Invalid packet length");
				}
				byte[] array2 = new byte[num];
				array.CopyTo(array2, 0);
				Reader.GetStream().ReadExactlyAsync(array2.AsMemory(15), cancellationTokenSource.Token).AsTask()
					.GetAwaiter()
					.GetResult();
				list.Add(array2);
				if (BitConverter.ToUInt16(array2, 1) == 591)
				{
					return list;
				}
			}
			throw new InvalidOperationException("No raid directory notification");
		}

		public void Dispose()
		{
			Session.TcpClient.Dispose();
			Reader.Dispose();
		}
	}

	public class RaidJoinCharacters : DispatchProxy
	{
		public bool SameAccount;

		protected override object Invoke(MethodInfo method, object[] args)
		{
			if (method.Name != "GetById")
			{
				throw new InvalidOperationException("Unexpected character dependency: " + method.Name);
			}
			int num = (int)args[0];
			CharacterRecord characterRecord = new CharacterRecord();
			characterRecord.CharacterId = num;
			characterRecord.AccountId = (SameAccount ? 1 : num);
			characterRecord.Name = new byte[1] { 65 };
			return characterRecord;
		}
	}

	private sealed class RaidTestDatabase : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), "a21-raid-protocol-" + Guid.NewGuid().ToString("N") + ".db");
		public GameDatabase Database { get; }

		public RaidTestDatabase() => Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);

		public void Dispose()
		{
			using (var connection = new SqliteConnection(Database.ConnectionString))
				SqliteConnection.ClearPool(connection);
			foreach (var suffix in new[] { "", "-wal", "-shm" })
				if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
		}
	}

	private sealed class RaidJoinFixture : IDisposable
	{
		public readonly RaidWireClient Leader = new RaidWireClient(71);

		public readonly RaidWireClient Applicant = new RaidWireClient(72);

		public readonly RaidWireClient Third = new RaidWireClient(73);

		public readonly SessionDirectory Sessions = new SessionDirectory();

		public readonly RaidManager Raids = new RaidManager();

		public readonly ICharacterRepository Characters = DispatchProxy.Create<ICharacterRepository, RaidJoinCharacters>();

		public readonly RaidHandler Handler;

		public readonly PartyHandler Peers;

		public readonly RaidSnapshot Original;

		private readonly RaidTestDatabase _database = new RaidTestDatabase();

		public long Now;

		public RaidJoinFixture()
		{
			RaidWireClient[] array = new RaidWireClient[3] { Leader, Applicant, Third };
			foreach (RaidWireClient raidWireClient in array)
			{
				Ready(raidWireClient);
				Sessions.Register(raidWireClient.Session.Player.CharacterId, raidWireClient.Session);
			}
			Original = Raids.Create(new byte[1] { 65 }, Leader.Member, 200);
			Handler = new RaidHandler(Characters, Sessions, Raids);
			Peers = new PartyHandler(new PartyManager(), Characters, Sessions, null, null, null, null, _database.Database);
			Peers.RaidPeerClockMilliseconds = () => Now;
			Peers.AttachRaidHandler(Handler);
			Handler.RaidPeerRequestAsync = Peers.RequestRaidPeerAsync;
		}

		public static void Ready(RaidWireClient client)
		{
			client.Session.Player.Name = new byte[1] { 65 };
			client.Session.Player.CurTownId = 19;
			client.Session.Player.CurAreaId = 1;
			client.Session.Player.UserState = 0;
			client.Session.Player.TownPresenceReady = true;
		}

		public void Apply()
		{
			Handler.HandleRaidJoinRequest(Applicant.Session, new GamePacketHeader
			{
				type = 868
			}, new byte[3] { 200, 71, 0 }).GetAwaiter().GetResult();
		}

		public void Respond(RaidWireClient recipient, ushort requester, byte[] body = null)
		{
			Peers.Handle_RES_PEER(recipient.Session, new GamePacketHeader
			{
				type = 11
			}, body ?? RaidPeerReply(requester)).GetAwaiter().GetResult();
		}

		public void Dispose()
		{
			Peers.Dispose();
			Leader.Dispose();
			Applicant.Dispose();
			Third.Dispose();
			_database.Dispose();
		}
	}

	private static void CheckRaidChannelEvent(ref int failures)
	{
		byte[] empty = EventInfoBodyBuilder.Build(null);
		Check("empty event snapshot enables the raid channel with a complete empty record",
			empty.Length == 18 && BitConverter.ToUInt16(empty, 0) == 1
			&& BitConverter.ToUInt16(empty, 2) == EventInfoBodyBuilder.RaidChannelEventId
			&& empty.Skip(4).All(value => value == 0), ref failures);
		var configured = new GameEventInfoEntry { EventId = EventInfoBodyBuilder.RaidChannelEventId, Unknown0 = 17 };
		byte[] existing = EventInfoBodyBuilder.Build(new GameEventInfoSnapshot { Events = new[] { configured } });
		Check("existing raid event is preserved without a duplicate",
			existing.Length == 18 && BitConverter.ToUInt16(existing, 0) == 1
			&& BitConverter.ToUInt32(existing, 4) == 17, ref failures);
		var full = Enumerable.Repeat(new GameEventInfoEntry { EventId = 1 }, ushort.MaxValue).ToArray();
		byte[] bounded = EventInfoBodyBuilder.Build(new GameEventInfoSnapshot { Events = full });
		Check("full event snapshot reserves a raid record without overflowing the wire count",
			BitConverter.ToUInt16(bounded, 0) == ushort.MaxValue
			&& bounded.Length == 3 + 15 * ushort.MaxValue
			&& BitConverter.ToUInt16(bounded, bounded.Length - 16) == EventInfoBodyBuilder.RaidChannelEventId,
			ref failures);
	}

	private static void CheckAntonTimerParsing(ref int failures)
	{
		const string source = """
[PHASE TIME OVER]
100 200
[PHASE]
[TRIGGER]
[ON CHANGE DUNGEON STATE]
221
[CHECK DUNGEON STATE]
221 `open`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 221 120
[RESERVE DUNGEON STATE]
221 `open` 240
[/BEHAVIOR]
[/PHASE]
[PHASE]
[TRIGGER]
[CHECK DUNGEON STATE]
221 `open`
[CHECK TIMER END]
1 221
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 221 45
[SET TIMER]
1 221 invalid
[/BEHAVIOR]
[/PHASE]
""";

		RaidEtcFile parsed = RaidEtcFile.Parse(source);
		Check("synthetic Anton phase limits preserve order",
			parsed.PhaseTimeOverSeconds.SequenceEqual(new[] { 100, 200 }), ref failures);
		Check("synthetic Anton timers preserve phase, trigger and source order",
			parsed.Phases[0].TimerDirectives.Count == 1
			&& parsed.Phases[1].TimerDirectives.Count == 1
			&& parsed.Phases[0].TimerDirectives[0].Seconds == 120
			&& parsed.Phases[0].TimerDirectives[0].Trigger.Kind == RaidEtcTriggerKind.DungeonOpened
			&& parsed.Phases[1].TimerDirectives[0].Seconds == 45
			&& parsed.Phases[1].TimerDirectives[0].Trigger.Kind == RaidEtcTriggerKind.TimerEnded,
			ref failures);
		Check("synthetic Anton reserve-open preserves trigger context",
			parsed.Phases[0].ReservedDungeonStates.Count == 1
			&& parsed.Phases[0].ReservedDungeonStates[0].DungeonId == 221
			&& parsed.Phases[0].ReservedDungeonStates[0].State == "open"
			&& parsed.Phases[0].ReservedDungeonStates[0].Seconds == 240
			&& parsed.Phases[0].ReservedDungeonStates[0].Trigger.Kind == RaidEtcTriggerKind.DungeonOpened,
			ref failures);
		Check("synthetic Anton malformed timer is diagnosable",
			parsed.ParseWarnings.Any(message => message.Contains("SET TIMER", StringComparison.Ordinal)),
			ref failures);
		RaidEtcFile malformedPhaseLimit = RaidEtcFile.Parse("[PHASE TIME OVER]\noops 200");
		Check("malformed first phase limit cannot shift the second phase slot",
			malformedPhaseLimit.PhaseTimeOverSeconds.SequenceEqual(new[] { 0, 200 })
			&& malformedPhaseLimit.ParseWarnings.Any(message => message.Contains("PHASE TIME OVER", StringComparison.Ordinal)),
			ref failures);

		RaidEtcFile live = RaidEtcFile.Parse(PvfArchiveAccessor.ReadText("etc/raid/anton.etc"));
		RaidTimerDirective[] liveTimers = live.Phases
			.SelectMany(phase => phase.TimerDirectives)
			.OrderBy(entry => entry.PhaseIndex)
			.ThenBy(entry => entry.SourceOrder)
			.ToArray();
		RaidReservedDungeonStateDirective[] liveReserves = live.Phases
			.SelectMany(phase => phase.ReservedDungeonStates)
			.OrderBy(entry => entry.PhaseIndex)
			.ThenBy(entry => entry.SourceOrder)
			.ToArray();
		Check("live Anton phase limits come from PVF",
			live.PhaseTimeOverSeconds.SequenceEqual(new[] { 2400, 2400 }), ref failures);
		Check("live Anton preserves every timer directive",
			liveTimers.Length == 33
			&& liveTimers.Select(entry => (entry.TimerType, entry.DungeonId, entry.Seconds)).Distinct().Count() == 31
			&& liveTimers.Select(entry => (entry.TimerType, entry.DungeonId)).Distinct().Count() == 23,
			ref failures);
		Check("live Anton preserves repeated equal timer directives",
			liveTimers.Count(entry => entry.TimerType == 2 && entry.DungeonId == 211 && entry.Seconds == 300) == 2
			&& liveTimers.Count(entry => entry.TimerType == 1 && entry.DungeonId == 216 && entry.Seconds == 360) == 2,
			ref failures);
		foreach (int dungeonId in Enumerable.Range(221, 4))
		{
			RaidTimerDirective[] dungeonTimers = liveTimers.Where(entry => entry.DungeonId == dungeonId).ToArray();
			Check($"live Anton hatchery {dungeonId} retains initial and repeat effects",
				dungeonTimers.Where(entry => entry.TimerType == 1).Select(entry => entry.Seconds).SequenceEqual(new[] { 120, 45 })
				&& dungeonTimers.Where(entry => entry.TimerType == 3).Select(entry => entry.Seconds).SequenceEqual(new[] { 120, 50 })
				&& dungeonTimers.Count(entry => entry.Seconds == 120 && entry.Trigger.Kind == RaidEtcTriggerKind.DungeonOpened) == 2
				&& dungeonTimers.Count(entry => (entry.Seconds == 45 || entry.Seconds == 50) && entry.Trigger.Kind == RaidEtcTriggerKind.TimerEnded) == 2,
				ref failures);
		}

		var expectedReserves = new (int DungeonId, int Seconds)[]
		{
			(216, 20),
			(212, 150),
			(214, 150),
			(221, 240),
			(222, 240),
			(223, 240),
			(224, 240),
		};
		Check("live Anton reserve-open directives come from PVF",
			liveReserves.Length == expectedReserves.Length
			&& liveReserves.All(entry => entry.State == "open")
			&& liveReserves.Select(entry => (entry.DungeonId, entry.Seconds)).SequenceEqual(expectedReserves),
			ref failures);
		foreach (string warning in live.ParseWarnings)
			Console.WriteLine($"[Anton timer parser warning] {warning}");
		Check("live Anton timer parser has no warnings", live.ParseWarnings.Count == 0, ref failures);
	}

	private static void CheckAntonTimerProjection(ref int failures)
	{
		const string validSource = """
[PHASE TIME OVER]
111 222
[PHASE]
[TRIGGER]
[CHECK DUNGEON STATE]
211 `open`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 211 481
[SET TIMER]
2 211 301
[SET TIMER]
3 211 241
[/BEHAVIOR]
[/PHASE]
[PHASE]
[TRIGGER]
[CHECK DUNGEON STATE]
221 `open`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 221 121
[SET TIMER]
3 221 122
[/BEHAVIOR]
[TRIGGER]
[CHECK TIMER END]
1 221
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 221 46
[/BEHAVIOR]
[TRIGGER]
[CHECK TIMER END]
3 221
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
3 221 51
[/BEHAVIOR]
[TRIGGER]
[CHECK DUNGEON STATE]
221 `clear`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
2 221 241
[RESERVE DUNGEON STATE]
221 `open` 241
[/BEHAVIOR]
[/PHASE]
""";
		var validWarnings = new List<string>();
		AntonRaidTimerConfiguration valid = AntonRaidTimerConfiguration.Create(
			RaidEtcFile.Parse(validSource), validWarnings.Add);
		Check("Anton timer projection freezes configured phase values",
			valid.GetPhaseLimitSeconds(0) == 111
			&& valid.GetPhaseLimitSeconds(1) == 222
			&& valid.GetDungeonActiveSeconds(0, 211) == 481
			&& valid.GetDungeonRecoverySeconds(0, 211) == 301
			&& valid.GetDungeonPassiveSeconds(0, 211) == 241,
			ref failures);
		Check("Anton timer projection separates hatchery trigger semantics",
			valid.GetHatcheryEffectInitialSeconds(1, 221) == 121
			&& valid.GetHatcheryEffectRepeatSeconds(1, 221) == 46
			&& valid.GetHatcheryEffectInitialSeconds(3, 221) == 122
			&& valid.GetHatcheryEffectRepeatSeconds(3, 221) == 51
			&& valid.GetDungeonRecoverySeconds(1, 221) == 241
			&& valid.GetReservedOpenSeconds(1, 221) == 241,
			ref failures);

		const string invalidSource = """
[PHASE TIME OVER]
0 -1
[PHASE]
[TRIGGER]
[CHECK DUNGEON STATE]
211 `open`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 211 0
[SET TIMER]
3 211 2147484
[SET TIMER]
1 212 301
[SET TIMER]
1 212 302
[/BEHAVIOR]
[TRIGGER]
[CHECK DUNGEON STATE]
214 `clear`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
2 214 151
[RESERVE DUNGEON STATE]
214 `open` 152
[/BEHAVIOR]
[/PHASE]
[PHASE]
[/PHASE]
""";
		var invalidWarnings = new List<string>();
		AntonRaidTimerConfiguration invalid = AntonRaidTimerConfiguration.Create(
			RaidEtcFile.Parse(invalidSource), invalidWarnings.Add);
		Check("Anton timer projection rejects invalid and conflicting durations",
			invalid.GetPhaseLimitSeconds(0) == 2400
			&& invalid.GetPhaseLimitSeconds(1) == 2400
			&& invalid.GetDungeonActiveSeconds(0, 211) == 480
			&& invalid.GetDungeonPassiveSeconds(0, 211) == 240
			&& invalid.GetDungeonActiveSeconds(0, 212) == 300,
			ref failures);
		int warningCountBeforeGetters = invalidWarnings.Count;
		Check("Anton recovery and reserve mismatch share fallbacks and one warning",
			invalid.GetDungeonRecoverySeconds(0, 214) == 150
			&& invalid.GetReservedOpenSeconds(0, 214) == 150
			&& invalidWarnings.Count(message => message.Contains("phase=0 dungeon=214 recovery/reserve", StringComparison.Ordinal)) == 1,
			ref failures);
		_ = invalid.GetDungeonRecoverySeconds(0, 214);
		_ = invalid.GetReservedOpenSeconds(0, 214);
		Check("Anton timer getters never emit repeated warnings",
			invalidWarnings.Count == warningCountBeforeGetters, ref failures);

		var liveWarnings = new List<string>();
		AntonRaidTimerConfiguration live = AntonRaidTimerConfiguration.Create(
			RaidEtcFile.Parse(PvfArchiveAccessor.ReadText("etc/raid/anton.etc")), liveWarnings.Add);
		Check("live Anton timer projection resolves PVF semantics",
			live.GetPhaseLimitSeconds(0) == 2400
			&& live.GetPhaseLimitSeconds(1) == 2400
			&& live.GetDungeonActiveSeconds(0, 211) == 480
			&& live.GetDungeonRecoverySeconds(0, 211) == 300
			&& live.GetDungeonPassiveSeconds(0, 211) == 240
			&& live.GetDungeonActiveSeconds(0, 216) == 360
			&& live.GetDungeonRecoverySeconds(0, 216) == 150
			&& live.GetDungeonPassiveSeconds(0, 216) == 120
			&& live.GetHatcheryOpenSeconds() == 180
			&& liveWarnings.Count == 0,
			ref failures);
	}

	private static void CheckAntonPhaseLimitBehavior(ref int failures)
	{
		const string source = """
[PHASE TIME OVER]
1000 2000
[PHASE]
[/PHASE]
[PHASE]
[/PHASE]
""";
		AntonRaidTimerConfiguration configuration = AntonRaidTimerConfiguration.Create(
			RaidEtcFile.Parse(source), _ => { });
		Check("distinct Anton phase limits remain independent",
			configuration.GetPhaseLimitSeconds(0) == 1000
			&& configuration.GetPhaseLimitSeconds(1) == 2000,
			ref failures);

		long clockMilliseconds = 0;
		var manager = new RaidManager(() => clockMilliseconds);
		RaidSnapshot created = manager.Create(
			new byte[] { 65 },
			new RaidMember
			{
				UserId = 81,
				CharacterId = 81,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1,
			},
			0);
		manager.TryBeginStart(created.LeaderUserId, out RaidSnapshot prepared);
		manager.TryCompletePreparation(prepared, out RaidSnapshot started);
		clockMilliseconds = 100_000;
		bool extended = manager.TryExtendPhaseTime(
			started.RaidId,
			1000,
			1000,
			300,
			out RaidSnapshot extendedRaid,
			out uint remainingSeconds);
		Check("phase extension caps configured remaining time",
			extended
			&& remainingSeconds == 1000
			&& extendedRaid.PhaseTimeExtensionSeconds == 100
			&& !manager.TryExtendPhaseTime(started.RaidId, 1000, 1000, 1, out _, out _),
			ref failures);
	}

	private static void CheckAntonConfiguredTimerBehavior(ref int failures)
	{
		const string source = """
[PHASE TIME OVER]
1000 2000
[PHASE]
[TRIGGER]
[CHECK DUNGEON STATE]
211 `open`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 211 481
[SET TIMER]
2 211 301
[SET TIMER]
3 211 241
[SET TIMER]
1 212 312
[SET TIMER]
2 212 151
[RESERVE DUNGEON STATE]
212 `open` 151
[SET TIMER]
1 214 314
[SET TIMER]
2 214 152
[RESERVE DUNGEON STATE]
214 `open` 152
[SET TIMER]
1 216 361
[SET TIMER]
2 216 153
[SET TIMER]
3 216 121
[RESERVE DUNGEON STATE]
216 `open` 21
[/BEHAVIOR]
[/PHASE]
[PHASE]
[TRIGGER]
[2PHASE INIT]
0
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
3 219 181
[/BEHAVIOR]
[TRIGGER]
[CHECK DUNGEON STATE]
221 `open`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 221 121
[SET TIMER]
3 221 122
[/BEHAVIOR]
[TRIGGER]
[CHECK TIMER END]
1 221
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
1 221 46
[/BEHAVIOR]
[TRIGGER]
[CHECK TIMER END]
3 221
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
3 221 51
[/BEHAVIOR]
[TRIGGER]
[CHECK DUNGEON STATE]
221 `clear`
[/TRIGGER]
[BEHAVIOR]
[SET TIMER]
2 221 241
[RESERVE DUNGEON STATE]
221 `open` 241
[/BEHAVIOR]
[/PHASE]
""";
		AntonRaidTimerConfiguration configuration = AntonRaidTimerConfiguration.Create(
			RaidEtcFile.Parse(source), _ => { });
		var clock = new ClockService();
		var raids = new RaidManager();
		var handler = new RaidHandler(
			DispatchProxy.Create<ICharacterRepository, UnusedDependency>(),
			new SessionDirectory(),
			raids,
			clock,
			configuration);
		RaidSnapshot created = raids.Create(
			new byte[] { 65 },
			new RaidMember
			{
				UserId = 82,
				CharacterId = 82,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1,
			},
			0);
		raids.TryBeginStart(created.LeaderUserId, out RaidSnapshot prepared);
		raids.TryCompletePreparation(prepared, out RaidSnapshot phaseOne);

		InvokeTimerMethod("ClearBlackFogSourceAsync", phaseOne, 4u);
		Dictionary<(byte TimerType, uint DungeonId), uint> phaseOneTimers = ReadProjectedTimers(
			handler.BuildRaidTimerSnapshotPackets(phaseOne, DateTime.UtcNow));
		Check("phase-one runtime registers injected PVF durations",
			phaseOneTimers[(1, 212)] == 312
			&& phaseOneTimers[(1, 214)] == 314
			&& phaseOneTimers[(2, 211)] == 301
			&& clock.GetDebugSnapshot().OneShotTimers == 4,
			ref failures);
		handler.CleanupRaidRuntimeState(phaseOne);

		raids.TryEnterPhaseBreak(phaseOne, out RaidSnapshot rewardState);
		raids.TryCompletePhase(rewardState.RaidId, out RaidSnapshot standby);
		raids.TryPrepareNextPhase(standby.LeaderUserId, out RaidSnapshot phaseTwoPrepared);
		raids.TryCompletePreparedNextPhase(phaseTwoPrepared, _ => true, out RaidSnapshot phaseTwo);
		InvokeTimerMethod("StartHatcheryOpenTimerAsync", phaseTwo);
		InvokeTimerMethod("StartHatcheryEffectTimersAsync", phaseTwo, 221u);
		InvokeTimerMethod("StartHatcheryRecoveryTimerAsync", phaseTwo, 221u);
		Dictionary<(byte TimerType, uint DungeonId), uint> phaseTwoTimers = ReadProjectedTimers(
			handler.BuildRaidTimerSnapshotPackets(phaseTwo, DateTime.UtcNow));
		Check("phase-two runtime registers injected initial and recovery durations",
			phaseTwoTimers[(3, 219)] == 181
			&& phaseTwoTimers[(1, 221)] == 121
			&& phaseTwoTimers[(3, 221)] == 122
			&& phaseTwoTimers[(2, 221)] == 241,
			ref failures);

		clock.CheckOnce(DateTime.UtcNow.AddSeconds(123));
		Dictionary<(byte TimerType, uint DungeonId), uint> repeatedTimers = null;
		bool repeated = SpinWait.SpinUntil(() =>
		{
			repeatedTimers = ReadProjectedTimers(handler.BuildRaidTimerSnapshotPackets(phaseTwo, DateTime.UtcNow));
			return repeatedTimers.TryGetValue((1, 221), out uint typeOne)
				&& typeOne is >= 45 and <= 46
				&& repeatedTimers.TryGetValue((3, 221), out uint typeThree)
				&& typeThree is >= 50 and <= 51;
		}, TimeSpan.FromSeconds(1));
		Check("hatchery callbacks continue with injected repeat durations",
			repeated, ref failures);

		InvokeTimerMethod("ClearHatcheryAsync", phaseTwo, 221u);
		Dictionary<(byte TimerType, uint DungeonId), uint> clearedTimers = ReadProjectedTimers(
			handler.BuildRaidTimerSnapshotPackets(phaseTwo, DateTime.UtcNow));
		Check("hatchery clear cancels its effects and retains only recovery",
			!clearedTimers.ContainsKey((1, 221))
			&& !clearedTimers.ContainsKey((3, 221))
			&& clearedTimers[(2, 221)] == 241,
			ref failures);
		handler.CleanupRaidRuntimeState(phaseTwo);

		void InvokeTimerMethod(string name, params object[] arguments)
		{
			MethodInfo method = typeof(RaidHandler).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
			((Task)method.Invoke(handler, arguments)).GetAwaiter().GetResult();
		}

		static Dictionary<(byte TimerType, uint DungeonId), uint> ReadProjectedTimers(
			IReadOnlyList<byte[]> packets)
		{
			return packets
				.Where(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_SET_TIMER)
				.ToDictionary(
					packet => (packet[15], BitConverter.ToUInt32(packet, 16)),
					packet => BitConverter.ToUInt32(packet, 24));
		}
	}

	private static void CheckGoldRateBoost(ref int failures)
	{
		int[] array = new int[5] { 10157782, 10157783, 10157784, 10157785, 10157786 };
		int[] original = new int[5] { 775, 145, 50, 25, 5 };
		RaidStateReward[] rewards = array.Select((int id, int i) => new RaidStateReward
		{
			ItemId = id,
			Weight = original[i],
			Flags = ((i == 0) ? 1 : 0)
		}).ToArray();
		double[] weights = AntonRaidGoldRate.GetWeights(1u, "squad_item", rewards);
		double[] second = new double[5] { 0.0, 644.4444444444445, 222.22222222222223, 111.11111111111111, 22.22222222222222 };
		RaidEtcPhase phase = RaidEtcFile.Parse(PvfArchiveAccessor.ReadText("etc/raid/anton.etc")).GetPhase(1);
		int rank;
		for (rank = 0; rank <= 3; rank++)
		{
			RaidStateReward[] source = phase.StateRewards.Where((RaidStateReward r) => r.RewardType == "squad_item" && r.Weight > 0).ToArray();
			RaidStateReward[] array2 = source.Where((RaidStateReward r) => r.State == rank).ToArray();
			RaidStateReward[] array3 = ((array2.Length != 0) ? array2 : source.Where((RaidStateReward r) => r.State == -1).ToArray());
			Check($"live PVF second-phase rank {rank} matches audited 22.5 percent gold pool", array3.Select((RaidStateReward r) => r.ItemId).SequenceEqual(array) && array3.Select((RaidStateReward r) => r.Weight).SequenceEqual(original), ref failures);
			double[] liveWeights = AntonRaidGoldRate.GetWeights(1u, "squad_item", array3);
			Check($"live PVF second-phase rank {rank} targets 100 percent", liveWeights.Zip(second, (double a, double b) => Math.Abs(a - b) < 1E-09).All((bool x) => x), ref failures);
			Check($"live PVF second-phase rank {rank} every selectable reward is actually gold", array3.Where((RaidStateReward r, int i) => liveWeights[i] > 0.0).All((RaidStateReward r) => StackableItemProvider.Load(r.ItemId).UpgradableLegacyRewards.Where((BoosterRewardEntry e) => e.ItemId >= 0 && e.Weight > 0).All((BoosterRewardEntry e) => AntonRaidRewardProvider.GetSquadDisplayFlags((uint)e.ItemId, ItemMetadataResolver.Resolve(e.ItemId)) == 1)), ref failures);
		}
		Check("second phase gold targets 100 percent preserving subtype ratios", weights.Zip(second, (double a, double b) => Math.Abs(a - b) < 1E-09).All((bool x) => x), ref failures);
		Check("first phase reward weights unchanged", AntonRaidGoldRate.GetWeights(0u, "squad_item", rewards).SequenceEqual(((IEnumerable<int>)original).Select((Func<int, double>)((int x) => x))), ref failures);
		Check("four-player cards unchanged", AntonRaidGoldRate.GetWeights(1u, "party_card", rewards).SequenceEqual(((IEnumerable<int>)original).Select((Func<int, double>)((int x) => x))), ref failures);
		Check("gold-coin weights unchanged", AntonRaidGoldRate.GetWeights(1u, "gold", rewards).SequenceEqual(((IEnumerable<int>)original).Select((Func<int, double>)((int x) => x))), ref failures);
		int[] array4 = new int[5];
		for (int num = 0; num < 225000; num++)
		{
			array4[AntonRaidGoldRate.Select(weights, ((double)num + 0.5) / 225000.0)]++;
		}
		Check("deterministic full selection range contains only gold and matches every subtype", array4.SequenceEqual(new int[5] { 0, 145000, 50000, 25000, 5000 }), ref failures);
		Check("target is fixed 100 percent rather than an additive or relative boost", AntonRaidGoldRate.BoostWeights(new double[2] { 10.0, 90.0 }, new bool[2] { false, true }).SequenceEqual(new double[2] { 0.0, 100.0 }), ref failures);
		Check("no gold pool remains unchanged", AntonRaidGoldRate.BoostWeights(new double[2] { 10.0, 90.0 }, new bool[2]).SequenceEqual(new double[2] { 10.0, 90.0 }), ref failures);
		Check("all gold pool remains unchanged", AntonRaidGoldRate.BoostWeights(new double[2] { 10.0, 90.0 }, new bool[2] { true, true }).SequenceEqual(new double[2] { 10.0, 90.0 }), ref failures);
		Check("empty pool selection falls back", AntonRaidGoldRate.Select(Array.Empty<double>(), 0.0) == -1, ref failures);
		RaidStateReward[] rewards2 = new int[4] { 10094739, 10096324, 10094738, 10096325 }.Select((int id, int i) => new RaidStateReward
		{
			ItemId = id,
			Weight = (new int[4] { 86, 7, 5, 2 })[i]
		}).ToArray();
		Check("fallback gold also targets 100 percent", Math.Abs(AntonRaidGoldRate.GetWeights(1u, "squad_item", rewards2).Skip(1).Sum() - 100.0) < 1E-09, ref failures);
	}

	private static void CheckExclusiveGoldRewards(ref int failures)
	{
		HashSet<uint> exclusive = new HashSet<uint>();
		(int, int)[] array = new(int, int)[6]
		{
			(10157775, 61),
			(10157784, 31),
			(10157785, 10),
			(10157776, 19),
			(10157786, 1),
			(10157783, 1)
		};
		uint[] array3;
		for (int i = 0; i < array.Length; i++)
		{
			(int, int) tuple = array[i];
			uint[] array2 = (StackableItemProvider.Load(tuple.Item1)?.UpgradableLegacyRewards)?.Where((BoosterRewardEntry e) => e.ItemId > 0 && e.Weight > 0).Select((BoosterRewardEntry e) => (uint)e.ItemId).Distinct()
				.ToArray() ?? Array.Empty<uint>();
			Check($"audited Anton pool {tuple.Item1} retains {tuple.Item2} distinct actual items", array2.Length == tuple.Item2, ref failures);
			array3 = array2;
			foreach (uint num2 in array3)
			{
				exclusive.Add(num2);
				ItemMetadata item = ItemMetadataResolver.Resolve((int)num2);
				Check($"Anton exclusive actual item {num2} is gold with valid metadata", AntonRaidRewardProvider.GetSquadDisplayFlags(num2, item) == 1, ref failures);
			}
		}
		Check("gold allowlist covers exactly 102 equipment, 20 cards and one soul fragment", exclusive.Count == 123, ref failures);
		int[] array4 = new int[4] { 10157777, 10157782, 10094739, 10094784 };
		foreach (int num3 in array4)
		{
			List<BoosterRewardEntry> list = StackableItemProvider.Load(num3)?.UpgradableLegacyRewards;
			Check($"generic epic pool {num3} cannot promote nonexclusive rewards", list != null && list.Count > 0 && list.Where((BoosterRewardEntry e) => e.ItemId > 0 && !exclusive.Contains((uint)e.ItemId)).All((BoosterRewardEntry e) => AntonRaidRewardProvider.GetSquadDisplayFlags((uint)e.ItemId, ItemMetadataResolver.Resolve(e.ItemId)) == 0), ref failures);
		}
		Check("unknown or mismatched metadata never becomes gold", AntonRaidRewardProvider.GetSquadDisplayFlags(10095702u, null) == 0 && AntonRaidRewardProvider.GetSquadDisplayFlags(101000497u, new ItemMetadata
		{
			ItemKind = "stackable"
		}) == 0 && AntonRaidRewardProvider.GetSquadDisplayFlags(10095702u, new ItemMetadata
		{
			ItemKind = "equipment",
			Rarity = 4
		}) == 0, ref failures);
		array3 = new uint[7] { 10093974u, 3330u, 10157782u, 10157783u, 10157784u, 0u, 4294967295u };
		foreach (uint num4 in array3)
		{
			ItemMetadata item2 = ((num4 > int.MaxValue || num4 == 0) ? null : ItemMetadataResolver.Resolve((int)num4));
			Check($"magic stones, tickets, containers and invalid id {num4} are not gold", AntonRaidRewardProvider.GetSquadDisplayFlags(num4, item2) == 0, ref failures);
		}
		uint[] samples = new uint[10] { 31128u, 10093974u, 10095702u, 101000497u, 10094747u, 10095701u, 100300212u, 108040274u, 3330u, 0u };
		byte[] expected = new byte[10] { 0, 0, 1, 1, 1, 1, 1, 1, 0, 0 };
		RaidRewardEntry[] rows = (from num5 in Enumerable.Range(0, 20)
			select new RaidRewardEntry
			{
				UserId = (ushort)(num5 + 1),
				CardType = 1,
				ItemId = samples[num5 % 10],
				Quantity = (uint)(num5 + 1),
				Flags = AntonRaidRewardProvider.GetSquadDisplayFlags(samples[num5 % 10], (samples[num5 % 10] == 0) ? null : ItemMetadataResolver.Resolve((int)samples[num5 % 10]))
			}).ToArray();
		byte[] body = RaidPacketBuilder.BuildRaidRewardList(3u, rows);
		Check("twenty actual reward rows put exclusive gold flags at the native wire offset", body.Length == 202 && Enumerable.Range(0, 20).All((int num5) => body[2 + 10 * num5 + 3] == expected[num5 % 10]), ref failures);
		Check("gold classification preserves twenty reward identities and quantities", Enumerable.Range(0, 20).All((int num5) => BitConverter.ToUInt16(body, 2 + 10 * num5) == rows[num5].UserId && body[2 + 10 * num5 + 2] == 1 && BitConverter.ToUInt32(body, 2 + 10 * num5 + 4) == rows[num5].ItemId && BitConverter.ToUInt16(body, 2 + 10 * num5 + 8) == rows[num5].Quantity), ref failures);
	}

	private static void CheckRewardFlags(ref int failures)
	{
		ItemMetadata item = new ItemMetadata
		{
			ItemKind = "equipment",
			Rarity = 4
		};
		Check("generic epic rarity no longer grants a gold card", AntonRaidRewardProvider.GetSquadDisplayFlags(31128u, item) == 0, ref failures);
		Check("actual soul fragment grants a gold card", AntonRaidRewardProvider.GetSquadDisplayFlags(10095702u, new ItemMetadata
		{
			ItemKind = "stackable"
		}) == 1, ref failures);
		Check("magic stone and unknown metadata are not promoted", AntonRaidRewardProvider.GetSquadDisplayFlags(10093974u, new ItemMetadata
		{
			ItemKind = "stackable",
			Rarity = 4
		}) == 0 && AntonRaidRewardProvider.GetSquadDisplayFlags(0u, null) == 0, ref failures);
		ItemMetadata itemMetadata = ItemMetadataResolver.Resolve(31128);
		Check("current PVF epic reward sample has equipment rarity four", itemMetadata.ItemKind == "equipment" && itemMetadata.Rarity == 4, ref failures);
		CheckExclusiveGoldRewards(ref failures);
		CheckGoldRateBoost(ref failures);
		RaidEtcPhase raidEtcPhase = new RaidEtcPhase();
		raidEtcPhase.StateRewards.Add(new RaidStateReward
		{
			RewardType = "squad_item",
			State = -1,
			Weight = 3,
			ItemId = 10157782,
			Flags = 0
		});
		raidEtcPhase.StateRewards.Add(new RaidStateReward
		{
			RewardType = "squad_item",
			State = -1,
			Weight = 1,
			ItemId = 10157783,
			Flags = 1
		});
		RaidRewardEntry[] rows = new RaidRewardEntry[20];
		for (int i = 0; i < rows.Length; i++)
		{
			bool flag = i % 2 == 1;
			Check("weighted selection succeeds for ordinary/special boundary", raidEtcPhase.TrySelectReward("squad_item", 0, flag ? 3 : 2, out var reward), ref failures);
			uint num = AntonRaidRewardProvider.ProjectRewardContainer(reward, out var flags);
			Check("same selected container retains its own flag", num == (uint)(flag ? 10157783 : 10157782) && (uint)flags == (flag ? 1u : 0u), ref failures);
			rows[i] = new RaidRewardEntry
			{
				UserId = (ushort)(i + 1),
				CardType = 1,
				Flags = flags,
				ItemId = num,
				Quantity = 1u
			};
		}
		byte[] body = RaidPacketBuilder.BuildRaidRewardList(3u, rows);
		Check("twenty-player reward remains 202 bytes", body.Length == 202 && body[0] == 3 && body[1] == 20, ref failures);
		Check("gold flag uses fourth byte of every ten-byte row, not card slot", Enumerable.Range(0, 20).All((int num2) => body[2 + 10 * num2 + 2] == 1 && body[2 + 10 * num2 + 3] == num2 % 2), ref failures);
		Check("ordinary and special rewards preserve item and count", Enumerable.Range(0, 20).All((int num2) => BitConverter.ToUInt32(body, 2 + 10 * num2 + 4) == rows[num2].ItemId && BitConverter.ToUInt16(body, 2 + 10 * num2 + 8) == 1), ref failures);
		bool condition = false;
		try
		{
			AntonRaidRewardProvider.ProjectRewardContainer(new RaidStateReward
			{
				ItemId = 1,
				Flags = 256
			}, out var _);
		}
		catch (OverflowException)
		{
			condition = true;
		}
		Check("reward flag overflow is rejected without truncation", condition, ref failures);
	}

	private static void CheckWaitingPlayers(ref int failures)
	{
		Guid sessionId = Guid.NewGuid();
		RaidMember raidMember = new RaidMember
		{
			UserId = 17,
			CharacterId = 4u,
			SessionId = sessionId
		};
		PlayerContext playerContext = new PlayerContext();
		playerContext.UserId = 17;
		playerContext.CharacterId = 4;
		playerContext.Level = 86;
		playerContext.Job = 2;
		playerContext.GrowType = 49;
		playerContext.Name = new byte[4] { 178, 226, 202, 212 };
		PlayerContext playerContext2 = playerContext;
		Check("waiting projects live identity not character id as uid", RaidHandler.TryProjectWaitingPlayer(raidMember, sessionId, playerContext2, 200, out var player) && player.UserId == 17 && player.Level == 86 && player.ServerIndex == 1, ref failures);
		RaidWaitingPlayerSnapshot player2 = new RaidWaitingPlayerSnapshot
		{
			UserId = 19,
			ChannelId = 201,
			ServerIndex = 1,
			Level = 85,
			Job = 3,
			GrowType = 18,
			NameBytes = new byte[1] { 65 }
		};
		RaidWaitingPlayerSnapshot raidWaitingPlayerSnapshot = player2;
		byte[] array = RaidWaitingPacketBuilder.Build(new RaidWaitingPlayerSnapshot[2] { player, raidWaitingPlayerSnapshot });
		Check("waiting uses current A21 notification and 39-byte records", array.Length == 97 && array[0] == 0 && BitConverter.ToUInt16(array, 1) == 747 && BitConverter.ToUInt32(array, 3) == array.Length && BitConverter.ToUInt32(array, 15) == 2, ref failures);
		byte[] source = array.Skip(19).Take(39).ToArray();
		Check("waiting field order and raw name match client reader", source.Take(9).SequenceEqual(new byte[9] { 17, 0, 200, 1, 86, 0, 49, 2, 0 }) && source.Skip(9).Take(4).SequenceEqual(playerContext2.Name) && source.Skip(13).All((byte value) => value == 0), ref failures);
		Check("second waiting row is aligned", array.Skip(58).Take(10).SequenceEqual(new byte[10] { 19, 0, 201, 1, 85, 0, 18, 3, 0, 65 }), ref failures);
		Check("empty waiting list still delivers refresh notification", RaidWaitingPacketBuilder.Build(Array.Empty<RaidWaitingPlayerSnapshot>()).Length == 19, ref failures);
		Check("waiting repeat is deterministic", array.SequenceEqual(RaidWaitingPacketBuilder.Build(new RaidWaitingPlayerSnapshot[2] { player, raidWaitingPlayerSnapshot })), ref failures);
		playerContext2.Name[0] = 65;
		Check("waiting projection owns name bytes", player.NameBytes[0] == 178, ref failures);
		Check("waiting rejects old session", !RaidHandler.TryProjectWaitingPlayer(raidMember, Guid.NewGuid(), playerContext2, 200, out player2), ref failures);
		playerContext2.CharacterId = 5;
		Check("waiting rejects switched character", !RaidHandler.TryProjectWaitingPlayer(raidMember, sessionId, playerContext2, 200, out player2), ref failures);
		playerContext2.CharacterId = 4;
		playerContext2.UserId = 18;
		Check("waiting rejects reused uid", !RaidHandler.TryProjectWaitingPlayer(raidMember, sessionId, playerContext2, 200, out player2), ref failures);
		playerContext2.UserId = 17;
		playerContext2.Name = new byte[30];
		Check("waiting rejects invalid name instead of shifting rows", !RaidHandler.TryProjectWaitingPlayer(raidMember, sessionId, playerContext2, 200, out player2), ref failures);
		bool condition = false;
		try
		{
			RaidWaitingPacketBuilder.Build(new RaidWaitingPlayerSnapshot[1]
			{
				new RaidWaitingPlayerSnapshot
				{
					NameBytes = new byte[30]
				}
			});
		}
		catch (ArgumentException)
		{
			condition = true;
		}
		Check("waiting builder does not truncate invalid name", condition, ref failures);
		RaidManager raidManager = new RaidManager();
		raidManager.TryAddWaiting(200, raidMember);
		raidManager.TryAddWaiting(200, raidMember);
		Check("waiting repeated registration has one row", raidManager.GetWaitingList(200).Count == 1, ref failures);
		Check("waiting channel scope preserved", raidManager.GetWaitingList(201).Count == 0, ref failures);
		raidManager.TryRemoveWaiting(raidMember.UserId);
		Check("waiting removal empties source", raidManager.GetWaitingList(200).Count == 0, ref failures);
	}

	private static void CheckOrdinaryPartyRejoin(ref int failures)
	{
		RaidManager raidManager = new RaidManager();
		PartyManager parties = new PartyManager();
		RaidMember raidMember = new RaidMember
		{
			UserId = 41,
			CharacterId = 41u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidMember b = new RaidMember
		{
			UserId = 42,
			CharacterId = 42u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidSnapshot raidSnapshot = raidManager.Create(Array.Empty<byte>(), raidMember, 200);
		raidManager.TryAddMember(raidSnapshot.RaidId, b, out var raid);
		Party party = parties.CreateParty(P(raidMember)).Party;
		parties.Join(party.PartyId, P(b));
		raidManager.TryBeginStart(raidMember.UserId, out var raid2);
		Check("pending start cannot rewrite raid assignment", !raidManager.TrySynchronizeJoinedParty(raidMember.UserId, raidMember.SessionId, () => parties.GetPartySnapshot(party.PartyId), out raid), ref failures);
		raidManager.TryCompletePreparation(raid2, out raid);
		parties.Leave(b.UserId, b.SessionId);
		raidManager.TryAssignParty(b.UserId, b.UserId, 0u, out var raid3);
		Party soloParty = parties.CreateParty(P(b)).Party;
		Check("departure is solo and is not auto-assigned", raid3.Members.Single((RaidMember m) => m.UserId == b.UserId).PartyIndex == 0 && !raidManager.TrySynchronizeJoinedParty(b.UserId, b.SessionId, () => parties.GetPartySnapshot(soloParty.PartyId), out var _), ref failures);
		parties.Leave(b.UserId, b.SessionId);
		parties.Join(party.PartyId, P(b));
		Check("rejoin restores raid squad membership", raidManager.TrySynchronizeJoinedParty(raidMember.UserId, raidMember.SessionId, () => parties.GetPartySnapshot(party.PartyId), out var raid5) && raid5.Members.All((RaidMember m) => m.PartyIndex == 1), ref failures);
		Check("rejoin restores one situation group", RaidManager.BuildSituationGroups(raid5.Members).Count == 1, ref failures);
		Check("repeat broadcast does not mutate assignments", !raidManager.TrySynchronizeJoinedParty(raidMember.UserId, raidMember.SessionId, () => parties.GetPartySnapshot(party.PartyId), out raid), ref failures);
		Check("stale session cannot repair raid assignment", !raidManager.TrySynchronizeJoinedParty(raidMember.UserId, Guid.NewGuid(), () => parties.GetPartySnapshot(party.PartyId), out raid), ref failures);
		Check("retired party cannot repair raid assignment", !raidManager.TrySynchronizeJoinedParty(raidMember.UserId, raidMember.SessionId, () => (Party)null, out raid), ref failures);
		parties.Join(party.PartyId, new PartyMember
		{
			UserId = 99,
			CharacterId = 99,
			SessionId = Guid.NewGuid()
		});
		Check("mixed raid party is not silently assigned", !raidManager.TrySynchronizeJoinedParty(raidMember.UserId, raidMember.SessionId, () => parties.GetPartySnapshot(party.PartyId), out raid), ref failures);
		static PartyMember P(RaidMember m)
		{
			return new PartyMember
			{
				UserId = m.UserId,
				CharacterId = (int)m.CharacterId,
				SessionId = m.SessionId
			};
		}
	}

	public static int Run()
	{
		Console.WriteLine("=== A21_RAID_PROTOCOL selftest ===");
		int failures = 0;
		CheckAntonTimerParsing(ref failures);
		CheckAntonTimerProjection(ref failures);
		CheckAntonPhaseLimitBehavior(ref failures);
		CheckAntonConfiguredTimerBehavior(ref failures);
		CheckRaidChannelEvent(ref failures);
		foreach (int item in Enumerable.Range(210, 6).Concat(Enumerable.Range(218, 7)))
		{
			for (int i = 0; i < 4; i++)
			{
				Check($"raid {item} difficulty {i} has no random champions", Dungeon.GetChampionCount(item, i, 0, out var _) == 0, ref failures);
			}
			List<Dungeon.MonsterSumInfo> list = new List<Dungeon.MonsterSumInfo>
			{
				new Dungeon.MonsterSumInfo
				{
					Code = 1,
					Type = 0,
					IsBlocking = true
				},
				new Dungeon.MonsterSumInfo
				{
					Code = 2,
					Type = 3,
					IsBlocking = true
				}
			};
			Dungeon.PromoteChampions(list, 999, null, item);
			Check($"raid {item} forced promotion preserves ordinary and boss types", list[0].Type == 0 && list[1].Type == 3, ref failures);
		}
		Check("raid and commander chat both use raid routing", ChatHandler.IsRaidMessageMode(52) && ChatHandler.IsRaidMessageMode(53) && !ChatHandler.IsRaidMessageMode(2), ref failures);
		Check("commander chat requires current raid leader", ChatHandler.CanSendRaidMessage(53, 7, 7) && !ChatHandler.CanSendRaidMessage(53, 26, 7) && !ChatHandler.CanSendRaidMessage(53, 0, 0), ref failures);
		Check("ordinary raid member may send raid chat", ChatHandler.CanSendRaidMessage(52, 26, 7), ref failures);
		byte[] array = ChatHandler.BuildNotificationBody(53, 7, 0, new byte[1] { 65 });
		Check("commander notification preserves mode and author", array[0] == 53 && BitConverter.ToUInt16(array, 1) == 7 && array[8] == 65, ref failures);
		CheckRewardFlags(ref failures);
		CheckWaitingPlayers(ref failures);
		CheckOrdinaryPartyRejoin(ref failures);
		CheckRaidPartyLeave(ref failures);
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		string text = "您已加入攻坚队。";
		Encoding encoding = Encoding.GetEncoding(936);
		byte[] array2 = DungeonEntryHandler.BuildRaidSelectionRestrictionNotice();
		string s = "必须加入攻坚队并开始攻坚后才能进入地下城。";
		Check("entry restriction uses GBK notice without changing text", array2[0] == 0 && array2[15] == 0 && BitConverter.ToUInt16(array2, 1) == 503 && BitConverter.ToUInt32(array2, 16) == encoding.GetByteCount(s) && array2.Skip(20).SequenceEqual(encoding.GetBytes(s)), ref failures);
		byte[] array3 = ServerNoticeMessageBuilder.BuildRaidNotice(text, 0);
		Check("raid notice has GBK bytes and byte-count dstr", array3.Length == 21 && array3[0] == 0 && BitConverter.ToUInt32(array3, 1) == 16 && encoding.GetString(array3, 5, 16) == text, ref failures);
		Check("empty raid notice retains header", ServerNoticeMessageBuilder.BuildRaidNotice(null, 0).SequenceEqual(new byte[5]), ref failures);
		Encoding[] array4 = new Encoding[2]
		{
			encoding,
			Encoding.UTF8
		};
		foreach (Encoding encoding2 in array4)
		{
			Check("raid notice name decodes without replacement: " + encoding2.CodePage, ServerNoticeMessageBuilder.DecodeRaidNoticeName(encoding2.GetBytes("牢k的忠犬")) == "牢k的忠犬", ref failures);
		}
		Check("ordinary notice keeps global encoding", ServerNoticeMessageBuilder.Build(text, 0).Skip(5).SequenceEqual(ClientTextEncoding.GetBytes(text)), ref failures);
		CheckPhaseTwoLeaderOrder(ref failures);
		CheckRaidTownReturn(ref failures);
		CheckMemberClearCounts(ref failures);
		CheckMemberColumnRecipientIsolation(ref failures);
		CheckMemberColumnFooterPhases(ref failures);
		CheckMemberColumnNegotiation(ref failures);
		CheckMemberColumnTerminalStates(ref failures);
		CheckMemberColumnBatchSend(ref failures);
		CheckRaidDepartureNotifications(ref failures);
		CheckRaidTownJoinRefresh(ref failures);
		CheckRaidLeaderTransfer(ref failures);
		CheckRaidJoinConfirmation(ref failures);
		CheckSituationWire(ref failures);
		CheckRewardList(ref failures);
		CheckPartyRewardSelectionReplay(ref failures);
		CheckPartyRewardWireAndPersistence(ref failures);
		Check("failure result has current-client ten-byte layout and disables rewards", RaidPacketBuilder.BuildRaidResult(1u, 1u, 2400u, 4u, 0u, 1).SequenceEqual(new byte[10] { 1, 1, 96, 9, 0, 0, 4, 0, 0, 1 }), ref failures);
		Check("successful result preserves rank and reward eligibility", RaidPacketBuilder.BuildRaidResult(0u, 0u, 256u, 513u, 3u, 0).SequenceEqual(new byte[10] { 0, 0, 0, 1, 0, 0, 1, 2, 3, 0 }), ref failures);
		Check("death display saturates without shifting reward flag", RaidPacketBuilder.BuildRaidResult(1u, 0u, 0u, uint.MaxValue, 0u, 1).SequenceEqual(new byte[10] { 1, 0, 0, 0, 0, 0, 255, 255, 0, 1 }), ref failures);
		CheckPreparation(ref failures);
		CheckRaidPartyCreationMode(ref failures);
		CheckLiveRaidInviteMode(ref failures);
		CheckPhaseIsolation(ref failures);
		CheckRaidTimerRejoinAndTermination(ref failures);
		CheckLivePartyAssignment(ref failures);
		byte[] array5 = RaidPacketBuilder.BuildEntryCostInfo(new RaidEntryCostStatus[3]
		{
			new RaidEntryCostStatus
			{
				UserId = 4,
				Ready = true,
				OwnedCount = 9u
			},
			new RaidEntryCostStatus
			{
				UserId = 5,
				Ready = true,
				OwnedCount = 1u
			},
			new RaidEntryCostStatus
			{
				UserId = 10,
				Ready = false,
				OwnedCount = 0u
			}
		});
		Check("three material rows preserve client five-byte record boundaries", array5.SequenceEqual(new byte[19]
		{
			3, 0, 0, 0, 4, 0, 1, 9, 0, 5,
			0, 1, 1, 0, 10, 0, 0, 0, 0
		}), ref failures);
		Check("empty material list preserves u32 header", RaidPacketBuilder.BuildEntryCostInfo(Array.Empty<RaidEntryCostStatus>()).SequenceEqual(new byte[4]), ref failures);
		Check("material display count saturates without wrapping", RaidPacketBuilder.BuildEntryCostInfo(new RaidEntryCostStatus[1]
		{
			new RaidEntryCostStatus
			{
				UserId = ushort.MaxValue,
				Ready = true,
				OwnedCount = uint.MaxValue
			}
		}).SequenceEqual(new byte[9] { 1, 0, 0, 0, 255, 255, 1, 255, 255 }), ref failures);
		byte[] array6 = TownHandler.BuildRaidUserFinishLoadPacket();
		Check("raid finish-load notification has no body and uses the registered A21 opcode", array6.Length == 15 && array6[0] == 0 && BitConverter.ToUInt16(array6, 1) == 1213 && BitConverter.ToUInt32(array6, 3) == 15, ref failures);
		PlayerContext playerContext = new PlayerContext
		{
			CharacterId = 4,
			CurTownId = 19,
			UserState = 0,
			TownPresenceReady = true
		};
		Check("raid town arrival permits completion notification", TownHandler.ShouldNotifyRaidTownLoaded(10200, playerContext), ref failures);
		Check("ordinary channel does not receive raid completion notification", !TownHandler.ShouldNotifyRaidTownLoaded(10010, playerContext), ref failures);
		playerContext.TownPresenceReady = false;
		Check("unready town state does not unlock raid search", !TownHandler.ShouldNotifyRaidTownLoaded(10200, playerContext), ref failures);
		playerContext.TownPresenceReady = true;
		playerContext.UserState = 1;
		Check("non-town state does not unlock raid search", !TownHandler.ShouldNotifyRaidTownLoaded(10200, playerContext), ref failures);
		playerContext.UserState = 0;
		playerContext.CharacterId = 0;
		Check("missing character does not unlock raid search", !TownHandler.ShouldNotifyRaidTownLoaded(10200, playerContext), ref failures);
		byte[] array7 = RaidPacketBuilder.BuildPeerInvite(4, 2919);
		Check("raid invitation includes all three client-read tail fields", array7.SequenceEqual(new byte[13]
		{
			4, 0, 10, 103, 11, 0, 0, 0, 0, 0,
			0, 0, 0
		}), ref failures);
		Check("raid invitation preserves full inviter and peer widths", RaidPacketBuilder.BuildPeerInvite(ushort.MaxValue, -1).SequenceEqual(new byte[13]
		{
			255, 255, 10, 255, 255, 255, 255, 0, 0, 0,
			0, 0, 0
		}), ref failures);
		byte[] array8 = GamePacketEnvelopeBuilder.Build(0, 7, array7);
		Check("raid invitation uses notification envelope with 13-byte body", array8.Length == 28 && array8[0] == 0 && BitConverter.ToUInt16(array8, 1) == 7 && BitConverter.ToUInt32(array8, 3) == 28 && array8.Skip(15).SequenceEqual(array7), ref failures);
		byte[] body = new byte[8] { 0, 3, 0, 0, 0, 49, 50, 51 };
		Check("CREATE_RAID accepts captured dstr body", RaidHandler.TryReadTitle(body, out var title) && title.SequenceEqual("123"u8), ref failures);
		byte[] body2 = new byte[5];
		Check("CREATE_RAID accepts an empty title dstr", RaidHandler.TryReadTitle(body2, out var title2) && title2.Length == 0, ref failures);
		Check("CREATE_RAID rejects truncated dstr", !RaidHandler.TryReadTitle(new byte[6] { 0, 3, 0, 0, 0, 49 }, out var title3), ref failures);
		Check("CREATE_RAID rejects impossible dstr length", !RaidHandler.TryReadTitle(new byte[7] { 0, 9, 0, 0, 0, 49, 50 }, out title3), ref failures);
		RaidMemberSnapshot raidMemberSnapshot = new RaidMemberSnapshot();
		raidMemberSnapshot.UserId = 4;
		raidMemberSnapshot.CharacterId = 4u;
		raidMemberSnapshot.NameBytes = new byte[1] { 49 };
		raidMemberSnapshot.Job = 35;
		raidMemberSnapshot.GrowType = 0;
		raidMemberSnapshot.PartyIndex = 0;
		RaidMemberSnapshot raidMemberSnapshot2 = raidMemberSnapshot;
		RaidDirectoryEntry raidDirectoryEntry = new RaidDirectoryEntry();
		raidDirectoryEntry.RaidId = 13107204u;
		raidDirectoryEntry.TitleBytes = new byte[2] { 82, 49 };
		raidDirectoryEntry.State = 2u;
		raidDirectoryEntry.StateArgument = 1u;
		raidDirectoryEntry.Leader = raidMemberSnapshot2;
		raidDirectoryEntry.MemberCount = 1;
		RaidDirectoryEntry raidDirectoryEntry2 = raidDirectoryEntry;
		byte[] array9 = RaidPacketBuilder.BuildRaidDirectory(new RaidDirectoryEntry[1] { raidDirectoryEntry2 });
		Check("raid directory matches the complete-object reader and member count", array9.Length == 40 && BitConverter.ToUInt32(array9, 0) == 1 && BitConverter.ToUInt32(array9, 4) == 13107204 && BitConverter.ToUInt32(array9, 8) == 2 && array9[12] == 82 && array9[13] == 49 && array9[14] == 0 && array9[15] == 2 && array9[16] == 1 && BitConverter.ToUInt16(array9, 21) == 4 && array9[39] == 1, ref failures);
		byte[] array10 = RaidHandler.BuildRaidDirectoryPacket(new RaidDirectoryEntry[1] { raidDirectoryEntry2 });
		Check("actual directory sender targets RAID_LIST, not RAID_WAITING_LIST", array10[0] == 0 && array10.Length == 55 && BitConverter.ToUInt16(array10, 1) == 591 && BitConverter.ToUInt32(array10, 3) == 55 && array10.Skip(15).SequenceEqual(array9), ref failures);
		byte[] array11 = RaidHandler.BuildRaidDirectoryPacket(Array.Empty<RaidDirectoryEntry>());
		Check("empty directory uses the same dispatch and a u32 zero count", array11.Length == 19 && BitConverter.ToUInt16(array11, 1) == 591 && array11.Skip(15).SequenceEqual(new byte[4]), ref failures);
		byte[] array12 = RaidHandler.BuildRaidDirectoryPacket(new RaidDirectoryEntry[2] { raidDirectoryEntry2, raidDirectoryEntry2 });
		Check("multiple directory entries retain record boundaries", array12.Length == 91 && BitConverter.ToUInt32(array12, 15) == 2 && array12.Skip(19).Take(36).SequenceEqual(array12.Skip(55)), ref failures);
		byte[] array13 = RaidPacketBuilder.BuildRaidCreate(4u, new byte[2] { 82, 49 }, 0u, 0u, raidMemberSnapshot2, new RaidMemberSnapshot[1] { raidMemberSnapshot2 });
		Check("RAID_MODIFY uses the A21 compact raid object and member layout", array13.Length == 62 && BitConverter.ToUInt32(array13, 0) == 4 && BitConverter.ToUInt32(array13, 4) == 0 && BitConverter.ToUInt32(array13, 8) == 4 && BitConverter.ToInt32(array13, 12) == 2 && array13[16] == 82 && array13[17] == 49 && BitConverter.ToUInt16(array13, 25) == 4 && array13[27] == 1 && BitConverter.ToUInt32(array13, 37) == 4 && array13[43] == 1 && BitConverter.ToUInt16(array13, 44) == 4 && array13[52] == 35, ref failures);
		byte[] array14 = RaidPacketBuilder.BuildRaidMembersUpdate(4u, new RaidMemberSnapshot[1] { raidMemberSnapshot2 });
		byte[] array15 = RaidPacketBuilder.BuildRaidCreate(4u, new byte[2] { 82, 49 }, 2u, 1u, raidMemberSnapshot2, new RaidMemberSnapshot[1] { raidMemberSnapshot2 });
		Check("full raid refresh preserves Anton type and active phase", array15.Length == 62 && array15[18] == 0 && array15[19] == 2 && array15[20] == 1 && BitConverter.ToUInt32(array15, 21) == 0, ref failures);
		Check("raid member refresh uses RAID_MODIFY operation 3 layout", array14.Length == 27 && BitConverter.ToUInt32(array14, 0) == 4 && BitConverter.ToUInt32(array14, 4) == 3 && array14[8] == 1 && BitConverter.ToUInt16(array14, 9) == 4 && array14[11] == 1 && BitConverter.ToUInt32(array14, 21) == 4, ref failures);
		raidMemberSnapshot2.PartyIndex = 1;
		byte[] array16 = RaidPacketBuilder.BuildRaidMembersUpdate(4u, new RaidMemberSnapshot[1] { raidMemberSnapshot2 });
		Check("RAID_MODIFY compact member record preserves party assignment", array16.Length == 27 && array16[19] == 1, ref failures);
		Check("RAID_STATE first phase is exactly two bytes", RaidPacketBuilder.BuildRaidState(2u, 0u).SequenceEqual(new byte[2] { 2, 0 }), ref failures);
		Check("RAID_STATE second phase retains its argument", RaidPacketBuilder.BuildRaidState(2u, 1u).SequenceEqual(new byte[2] { 2, 1 }), ref failures);
		Check("RAID_STATE failure argument does not shift", RaidPacketBuilder.BuildRaidState(3u, 1u).SequenceEqual(new byte[2] { 3, 1 }), ref failures);
		Check("RAID_STATE rejects overflowing state", RejectsRange(() => RaidPacketBuilder.BuildRaidState(256u, 0u)), ref failures);
		Check("RAID_STATE rejects overflowing argument", RejectsRange(() => RaidPacketBuilder.BuildRaidState(2u, 256u)), ref failures);
		byte[] array17 = RaidPacketBuilder.BuildSetTimer(1u, 210u, 2400u, 1694564867u);
		Check("RAID_SET_TIMER follows current 13-byte client reader", array17.SequenceEqual(new byte[13]
		{
			1, 210, 0, 0, 0, 3, 2, 1, 101, 96,
			9, 0, 0
		}), ref failures);
		Check("RAID_SET_TIMER ready countdown is three seconds", RaidPacketBuilder.BuildSetTimer(0u, 0u, 3u, 1003u).SequenceEqual(new byte[13]
		{
			0, 0, 0, 0, 0, 235, 3, 0, 0, 3,
			0, 0, 0
		}), ref failures);
		Check("RAID_SET_TIMER rejects overflowing timer kind", RejectsRange(() => RaidPacketBuilder.BuildSetTimer(256u, 0u, 3u, 1003u)), ref failures);
		Check("RAID_REMAIN_TIME remains a byte and seconds", RaidPacketBuilder.BuildRemainTime(0, 2400u).SequenceEqual(new byte[5] { 0, 96, 9, 0, 0 }), ref failures);
		byte[] array18 = GamePacketEnvelopeBuilder.Build(0, 597, array17);
		Check("raid timer envelope retains A21 opcode and exact body length", array18.Length == 28 && BitConverter.ToUInt16(array18, 1) == 597 && BitConverter.ToUInt32(array18, 3) == 28 && array18.Skip(15).SequenceEqual(array17), ref failures);
		byte[] array19 = RaidPacketBuilder.BuildDungeonState(RaidHandler.AntonFirstPhaseInitialDungeonStates);
		Check("first phase list exposes seven dungeons, not zero", array19.SequenceEqual(new byte[41]
		{
			0, 7, 210, 0, 0, 0, 0, 211, 0, 0,
			0, 2, 212, 0, 0, 0, 2, 213, 0, 0,
			0, 2, 214, 0, 0, 0, 2, 215, 0, 0,
			0, 2, 216, 0, 0, 0, 2, 0, 0, 0,
			0
		}), ref failures);
		byte[] array20 = RaidPacketBuilder.BuildDungeonState(RaidHandler.AntonSecondPhaseInitialDungeonStates);
		Check("second phase list exposes its distinct dungeon IDs and states", array20.SequenceEqual(new byte[41]
		{
			0, 7, 218, 0, 0, 0, 0, 219, 0, 0,
			0, 0, 220, 0, 0, 0, 2, 221, 0, 0,
			0, 2, 222, 0, 0, 0, 2, 223, 0, 0,
			0, 2, 224, 0, 0, 0, 2, 0, 0, 0,
			0
		}), ref failures);
		Check("single dungeon list preserves trailing infection ID", RaidPacketBuilder.BuildDungeonState(220u, 3u, 224u).SequenceEqual(new byte[11]
		{
			0, 1, 220, 0, 0, 0, 3, 224, 0, 0,
			0
		}), ref failures);
		Check("dungeon state change uses six bytes and retains state", RaidPacketBuilder.BuildChangeDungeonState(212u, 3u).SequenceEqual(new byte[6] { 212, 0, 0, 0, 0, 3 }), ref failures);
		Check("empty dungeon list still carries the infection field", RaidPacketBuilder.BuildDungeonState(Array.Empty<KeyValuePair<uint, uint>>()).SequenceEqual(new byte[6]), ref failures);
		KeyValuePair<uint, uint>[] maxList = Enumerable.Repeat(new KeyValuePair<uint, uint>(210u, 0u), 255).ToArray();
		Check("maximum byte-sized dungeon list retains all records", RaidPacketBuilder.BuildDungeonState(maxList).Length == 1281 && RaidPacketBuilder.BuildDungeonState(maxList)[1] == byte.MaxValue, ref failures);
		Check("oversized dungeon list is rejected without truncation", RejectsRange(() => RaidPacketBuilder.BuildDungeonState(maxList.Concat(maxList.Take(1)).ToArray())), ref failures);
		Check("oversized initial dungeon state is rejected", RejectsRange(() => RaidPacketBuilder.BuildDungeonState(210u, 256u)), ref failures);
		Check("oversized changed dungeon state is rejected", RejectsRange(() => RaidPacketBuilder.BuildChangeDungeonState(210u, 256u)), ref failures);
		byte[] array21 = GamePacketEnvelopeBuilder.Build(0, 586, array19);
		Check("dungeon list envelope carries the complete 41-byte body", array21.Length == 56 && BitConverter.ToUInt32(array21, 3) == 56 && BitConverter.ToUInt16(array21, 1) == 586 && array21.Skip(15).SequenceEqual(array19), ref failures);
		Console.WriteLine((failures == 0) ? "A21_RAID_PROTOCOL selftest passed." : $"A21_RAID_PROTOCOL selftest failed: {failures}");
		return (failures != 0) ? 1 : 0;
	}

	private static void CheckLivePartyAssignment(ref int failures)
	{
		RaidManager raids = new RaidManager();
		PartyManager parties = new PartyManager();
		RaidMember[] members = Enumerable.Range(61, 5).Select((int id, int i) => new RaidMember
		{
			UserId = (ushort)id,
			CharacterId = (uint)id,
			SessionId = Guid.NewGuid(),
			PartyIndex = (ushort)((i < 2) ? 1u : ((i < 4) ? 2u : 0u))
		}).ToArray();
		RaidSnapshot raidSnapshot = raids.Create(Array.Empty<byte>(), members[0], 200);
		RaidSnapshot raid;
		foreach (RaidMember item in members.Skip(1))
		{
			raids.TryAddMember(raidSnapshot.RaidId, item, out raid);
		}
		foreach (IGrouping<ushort, RaidMember> item2 in from raidMember in members
			where raidMember.PartyIndex != 0
			group raidMember by raidMember.PartyIndex)
		{
			PartyMember partyMember = AsParty(item2.First());
			Party party = parties.CreateParty(partyMember).Party;
			foreach (RaidMember item3 in item2.Skip(1))
			{
				parties.Join(party.PartyId, AsParty(item3));
			}
			parties.UpdateSettings(partyMember.UserId, partyMember.SessionId, Array.Empty<byte>(), 4, new byte[12]
			{
				0, 0, 4, 255, 255, 255, 255, 5, 0, 2,
				0, 0
			});
		}
		raids.TryBeginStart(members[0].UserId, out var raid2);
		raids.TryCompletePreparation(raid2, out var raid3);
		List<Party> retired = new List<Party>();
		List<Party> formed = new List<Party>();
		bool touched = false;
		Check("nonleader live assignment does not reach party owner", !raids.TryAssignLiveParty(raid3, members[1].UserId, members[1].SessionId, members[0].UserId, 2u, (IReadOnlyList<RaidMember> _) => touched = true, out raid) && !touched, ref failures);
		Check("old leader session cannot assign live party", !raids.TryAssignLiveParty(raid3, members[0].UserId, Guid.NewGuid(), members[0].UserId, 2u, (IReadOnlyList<RaidMember> _) => touched = true, out raid) && !touched, ref failures);
		Check("leader move updates both owners and replaces affected generations", Move(raid3, 0, 2, out var raid4) && retired.Count == 2 && formed.Count == 2 && parties.GetPartyByUser(61).PartyId == parties.GetPartyByUser(63).PartyId && parties.GetPartyByUser(62).Count == 1 && parties.ArePreparedRaidPartiesReady(raid4.Members), ref failures);
		Check("stale roster request cannot undo a committed move", !Move(raid3, 0, 1, out raid), ref failures);
		int partyId = parties.GetPartyByUser(61).PartyId;
		Check("duplicate assignment keeps actual party generation", Move(raid4, 0, 2, out raid4) && retired.Count == 0 && formed.Count == 0 && parties.GetPartyByUser(61).PartyId == partyId, ref failures);
		Check("unassigned raid member can join a live small party", Move(raid4, 4, 2, out raid4) && parties.GetPartyByUser(65).Count == 4, ref failures);
		RaidSnapshot expected = raid4;
		Check("full destination rejects without changing either owner", !Move(raid4, 1, 2, out raid) && parties.GetPartyByUser(62).Count == 1, ref failures);
		Check("live unassignment clears actual membership", Move(expected, 0, 0, out raid4) && parties.GetPartyByUser(61) == null && parties.ArePreparedRaidPartiesReady(raid4.Members), ref failures);
		Party party2 = parties.CreateParty(AsParty(members[0])).Party;
		Check("live assignment cannot displace unrelated party", !Move(raid4, 0, 1, out raid) && parties.GetPartyByUser(61).PartyId == party2.PartyId, ref failures);
		parties.Leave(61);
		Check("live assignment can form a new singleton group", Move(raid4, 0, 3, out raid4) && parties.GetPartyByUser(61).Count == 1 && parties.ArePreparedRaidPartiesReady(raid4.Members), ref failures);
		Check("invalid small party index is rejected", !Move(raid4, 0, 11, out raid), ref failures);
		RaidSnapshot expected2 = raid4;
		Check("round trip assignment keeps old snapshots invalid", Move(raid4, 0, 1, out raid4) && Move(raid4, 0, 3, out raid4) && !Move(expected2, 0, 2, out raid), ref failures);
		Check("real-party commit rejection leaves raid assignment intact", !raids.TryAssignLiveParty(raid4, 61, members[0].SessionId, 61, 2u, (IReadOnlyList<RaidMember> _) => false, out raid) && raids.TryGetByRaidId(raid4.RaidId, out var raid5) && raid5.AssignmentVersion == raid4.AssignmentVersion, ref failures);
		raids.TryFailPhase(raid4, out var raid6);
		Check("failed raid cannot change live party", !Move(raid6, 0, 1, out raid), ref failures);
		static PartyMember AsParty(RaidMember raidMember)
		{
			return new PartyMember
			{
				UserId = raidMember.UserId,
				CharacterId = (int)raidMember.CharacterId,
				SessionId = raidMember.SessionId,
				Name = "live-raid"
			};
		}
		bool Move(RaidSnapshot expected3, int target, ushort index, out RaidSnapshot raid7)
		{
			return raids.TryAssignLiveParty(expected3, members[0].UserId, members[0].SessionId, members[target].UserId, index, (IReadOnlyList<RaidMember> roster) => parties.ReassignLiveRaidMember(roster, AsParty(members[target]), index, out retired, out formed), out raid7);
		}
	}

	private static void CheckRaidPartyCreationMode(ref int failures)
	{
		RaidManager raids = new RaidManager();
		PartyManager parties = new PartyManager();
		RaidMember raidMember = new RaidMember
		{
			UserId = 301,
			CharacterId = 301u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		PartyMember member = new PartyMember
		{
			UserId = raidMember.UserId,
			CharacterId = 301,
			SessionId = raidMember.SessionId
		};
		byte[] story = new byte[12]
		{
			0, 5, 4, 0, 0, 0, 0, 5, 0, 0,
			255, 255
		};
		byte[] title = new byte[2] { 65, 66 };
		PartyOpResult created = null;
		PartyOpResult result = null;
		Check("ordinary creation preserves explicit story settings", Apply() && result.Ok && created != null && result.Party.PartyInfoBlock.SequenceEqual(story), ref failures);
		RaidSnapshot raidSnapshot = raids.Create(Array.Empty<byte>(), raidMember, 200);
		Check("recruiting before start preserves ordinary settings", Apply() && result.Ok && created == null && result.Party.PartyInfoBlock.SequenceEqual(story), ref failures);
		Check("mode fixture begins preparation", raids.TryBeginStart(raidMember.UserId, out var raid), ref failures);
		Check("preparation upgrades existing story singleton to normal", Apply() && Normal() && created == null && result.Party.TitleBytes.SequenceEqual(title), ref failures);
		Check("normalized singleton satisfies raid preparation", raids.IsPreparationReady(raid, parties.ArePreparedRaidPartiesReady), ref failures);
		Check("mode fixture enters phase one", raids.TryCompletePreparation(raid, out var raid2), ref failures);
		parties.Leave(member.UserId, member.SessionId);
		Check("active raid singleton creation uses normal mode", Apply() && Normal() && created != null && result.Party.Count == 1, ref failures);
		int partyId = result.Party.PartyId;
		result.Party.IsSinglePlay = true;
		Check("active edit cannot downgrade mode or recreate party", Apply() && Normal() && created == null && result.Party.PartyId == partyId, ref failures);
		byte[] array = new byte[18]
		{
			1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0
		};
		BitConverter.GetBytes(2).CopyTo(array, 2);
		title.CopyTo(array, 6);
		Array.Copy(story, 2, array, 8, 10);
		byte[][] array2 = new byte[2][] { story, array };
		foreach (byte[] array3 in array2)
		{
			bool flag = SetPartyInfoRequest.TryParse(array3, out var request) && raids.TryCommitPartySettings(member.UserId, member.SessionId, delegate(IReadOnlyList<RaidMember> roster)
			{
				result = parties.SetPartyInfo(member, title, request.UserMax, request.Raw, roster, out created);
			});
			Check($"{array3.Length}-byte client story request is normalized after parsing", flag && Normal() && request.Raw[9] == 0 && result.Party.PartyId == partyId, ref failures);
		}
		byte[] array4 = new byte[2] { 0, 1 };
		foreach (byte b in array4)
		{
			byte[] array5 = PartyInfoNotiBuilder.Build(result.Party, b);
			Check($"party notify type {b} sends canonical normal settings", array5[5] == 0 && array5[11] == 4 && array5[18] == 2 && array5.Length == ((b == 0) ? 65 : 22), ref failures);
		}
		Check("caller story buffer is never changed", story[9] == 0 && story[1] == 5, ref failures);
		Guid sessionId = member.SessionId;
		member.SessionId = Guid.NewGuid();
		bool called = false;
		Check("stale raid session never enters party mutation", !raids.TryCommitPartySettings(member.UserId, member.SessionId, delegate
		{
			called = true;
		}) && !called, ref failures);
		member.SessionId = sessionId;
		Check("mode fixture completes first phase", raids.TryEnterPhaseBreak(raidSnapshot.RaidId, out raid2) && raids.TryCompletePhase(raidSnapshot.RaidId, out raid2), ref failures);
		Check("phase break party edit stays normal", Apply() && Normal(), ref failures);
		Check("mode fixture prepares second phase", raids.TryPrepareNextPhase(raidMember.UserId, out var raid3), ref failures);
		Check("second preparation party edit stays normal", Apply() && Normal(), ref failures);
		Check("mode fixture enters second phase", raids.TryCompletePreparedNextPhase(raid3, parties.ArePreparedRaidPartiesReady, out raid2), ref failures);
		parties.Leave(member.UserId, member.SessionId);
		Check("second phase singleton recreation stays normal", Apply() && Normal() && created != null, ref failures);
		RaidMember raidMember2 = new RaidMember
		{
			UserId = 302,
			CharacterId = 302u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		PartyMember partyMember = new PartyMember
		{
			UserId = 302,
			CharacterId = 302,
			SessionId = raidMember2.SessionId
		};
		RaidMember[] raidRoster = new RaidMember[2] { raidMember, raidMember2 };
		parties.Join(result.Party.PartyId, partyMember);
		Check("nonleader cannot update normalized party", !parties.SetPartyInfo(partyMember, title, 4, story, raidRoster, out created).Ok && created == null, ref failures);
		Check("unrelated party members block raid normalization", !parties.SetPartyInfo(member, title, 4, story, new RaidMember[1] { raidMember }, out created).Ok && created == null, ref failures);
		PartyManager partyManager = new PartyManager();
		Check("invalid payload creates no orphan party", !partyManager.SetPartyInfo(member, title, 4, new byte[3], raidRoster, out created).Ok && partyManager.PartyCount == 0 && created == null, ref failures);
		Check("invalid raid identity creates no orphan party", !partyManager.SetPartyInfo(member, title, 4, story, new RaidMember[1] { raidMember2 }, out created).Ok && partyManager.PartyCount == 0, ref failures);
		bool Apply()
		{
			return raids.TryCommitPartySettings(member.UserId, member.SessionId, delegate(IReadOnlyList<RaidMember> roster)
			{
				result = parties.SetPartyInfo(member, title, 4, story, roster, out created);
			});
		}
		bool Normal()
		{
			if (result != null && result.Ok && result.Party.PartyInfoBlock[9] == 2 && result.Party.UserMax == 4)
			{
				return !result.Party.IsSinglePlay;
			}
			return false;
		}
	}

	private static void CheckLiveRaidInviteMode(ref int failures)
	{
		CheckLiveRaidInviteGuards(ref failures);
		foreach (int phase in new[] { 0, 1 })
		foreach (int existing in new[] { 0, 1, 2 })
		{
			string label = $"live raid invite phase={phase} existing={existing}";
			using var inviter = new RaidWireClient(91);
			using var invitee = new RaidWireClient(92);
			var sessions = new SessionDirectory();
			foreach (var client in new[] { inviter, invitee })
			{
				RaidJoinFixture.Ready(client);
				sessions.Register(client.Session.Player.CharacterId, client.Session);
			}
			var raids = new RaidManager();
			var parties = new PartyManager();
			var a = inviter.Member; a.PartyIndex = 0;
			var b = invitee.Member; b.PartyIndex = 0;
			var raid = raids.Create(Array.Empty<byte>(), a, 200);
			raids.TryAddMember(raid.RaidId, b, out raid);
			raids.TryBeginStart(a.UserId, out var prepared);
			raids.TryCompletePreparation(prepared, out raid);
			if (phase == 1)
			{
				raids.TryEnterPhaseBreak(raid.RaidId, out raid);
				raids.TryCompletePhase(raid.RaidId, out raid);
				raids.TryPrepareNextPhase(a.UserId, out prepared);
				raids.TryCompletePreparedNextPhase(prepared, parties.ArePreparedRaidPartiesReady, out raid);
			}
			PartyMember AsParty(RaidMember m) => new PartyMember { UserId = m.UserId, CharacterId = (int)m.CharacterId, SessionId = m.SessionId };
			if (existing != 0)
			{
				var old = parties.CreateParty(AsParty(existing == 1 ? a : b)).Party;
				old.IsSinglePlay = true;
			}
			var characters = DispatchProxy.Create<ICharacterRepository, RaidJoinCharacters>();
			var handler = new RaidHandler(characters, sessions, raids);
			using var database = new RaidTestDatabase();
			using var peers = new PartyHandler(parties, characters, sessions, null, null, null, null, database.Database);
			peers.AttachRaidHandler(handler);
			Check(label + " records an exact ordinary invitation", parties.RecordInvite(b.UserId, b.SessionId, a.UserId, a.SessionId, out _), ref failures);
			var reply = new byte[7];
			BitConverter.GetBytes(a.UserId).CopyTo(reply, 0);
			BitConverter.GetBytes((int)a.UserId).CopyTo(reply, 3);
			peers.Handle_RES_PEER(invitee.Session, new GamePacketHeader { type = (ushort)CmdPacketTypeA21.RESPONSE_PEER }, reply).GetAwaiter().GetResult();
			var party = parties.GetPartyByUser(a.UserId);
			Check(label + " commits normal mode before publication", party != null && party.Count == 2 && party.PartyInfoBlock[9] == 2 && !party.IsSinglePlay && party.LeaderUserId == (existing == 2 ? b.UserId : a.UserId), ref failures);
			foreach (var client in new[] { inviter, invitee })
			{
				var packets = ReadAvailableRaidPackets(client).Where(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.PARTY_INFO && p.Length >= 80 && p[19] == 0).ToArray();
				Check(label + $" user={client.Session.Player.UserId} receives only normal formation mode", packets.Length > 0 && packets.All(p => p[33] == 2), ref failures);
			}
			var leader = party.GetMember(party.LeaderUserId);
			var departed = raids.LeaveNormalParty(leader.UserId, leader.SessionId, () => parties.Leave(leader.UserId, leader.SessionId), out var afterLeave);
			Check(label + " later departure stays unassigned solo", departed.Ok && parties.GetPartyByUser(leader.UserId) == null && afterLeave.Members.Single(m => m.UserId == leader.UserId).PartyIndex == 0, ref failures);
		}
	}

	private static void CheckLiveRaidInviteGuards(ref int failures)
	{
		var raids = new RaidManager();
		var parties = new PartyManager();
		var a = new PartyMember { UserId = 401, CharacterId = 401, SessionId = Guid.NewGuid() };
		var b = new PartyMember { UserId = 402, CharacterId = 402, SessionId = Guid.NewGuid() };
		RaidMember R(PartyMember p) => new RaidMember { UserId = p.UserId, CharacterId = (uint)p.CharacterId, SessionId = p.SessionId };
		PartyOpResult result = null;
		int commits = 0;
		bool Accept() => raids.TryCommitPartyJoin(a, b, roster => { commits++; result = parties.AcceptRaidAwareInvite(a, b, roster, out _); });
		Check("normal invite without pending approval cannot form a party", Accept() && !result.Ok && parties.PartyCount == 0, ref failures);
		var ordinary = parties.CreateParty(a).Party;
		var original = (byte[])ordinary.PartyInfoBlock.Clone();
		parties.RecordInvite(b.UserId, b.SessionId, a.UserId, a.SessionId, out _);
		Check("nonraid join preserves ordinary mode and title", Accept() && result.Ok && ordinary.PartyInfoBlock.SequenceEqual(original), ref failures);
		Check("duplicate acceptance cannot create another party", Accept() && !result.Ok && parties.PartyCount == 1 && ordinary.Count == 2, ref failures);
		parties.Leave(a.UserId, a.SessionId); parties.Leave(b.UserId, b.SessionId);
		var raid = raids.Create(Array.Empty<byte>(), R(a), 200);
		commits = 0;
		Check("nonmember cannot join a raid squad through normal invitation", !Accept() && commits == 0 && parties.PartyCount == 0, ref failures);
		var other = raids.Create(Array.Empty<byte>(), R(b), 200);
		Check("different raids cannot be mixed through normal invitation", !Accept() && commits == 0, ref failures);
		raids.Leave(b.UserId);
		raids.TryAddMember(raid.RaidId, R(b), out raid);
		var currentSession = b.SessionId; b.SessionId = Guid.NewGuid();
		Check("stale raid session cannot enter the party mutation", !Accept() && commits == 0, ref failures);
		b.SessionId = currentSession;
		raids.TryBeginStart(a.UserId, out var prepared);
		Check("normal invitation cannot bypass frozen raid preparation", !Accept() && commits == 0, ref failures);
		raids.TryCompletePreparation(prepared, out raid);
		var existing = parties.CreateParty(a).Party;
		var outsider = new PartyMember { UserId = 403, CharacterId = 403, SessionId = Guid.NewGuid() };
		parties.Join(existing.PartyId, outsider);
		parties.RecordInvite(b.UserId, b.SessionId, a.UserId, a.SessionId, out _);
		Check("mixed existing party is rejected before joining or changing mode", Accept() && !result.Ok && existing.Count == 2 && existing.PartyInfoBlock[9] == 0 && parties.GetPartyByUser(b.UserId) == null, ref failures);
		parties.Leave(outsider.UserId, outsider.SessionId);
		Check("valid current invitation commits normal mode atomically", Accept() && result.Ok && existing.Count == 2 && existing.PartyInfoBlock[9] == 2, ref failures);
	}

	private static void CheckPreparation(ref int failures)
	{
		RaidManager raids = new RaidManager();
		PartyManager parties = new PartyManager();
		RaidMember[] members = (from id in Enumerable.Range(41, 4)
			select new RaidMember
			{
				UserId = (ushort)id,
				CharacterId = (uint)id,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1
			}).ToArray();
		RaidSnapshot raidSnapshot = raids.Create(Array.Empty<byte>(), members[0], 200);
		RaidSnapshot raid;
		foreach (RaidMember item in members.Skip(1))
		{
			if (!raids.TryAddMember(raidSnapshot.RaidId, item, out raid))
			{
				throw new InvalidOperationException("raid fixture");
			}
		}
		Check("ordinary acceptance still requires a pending invitation", !parties.AcceptInvite(members[1].UserId, members[1].SessionId, members[0].UserId, members[0].SessionId, PartyMemberFor(members[0]), PartyMemberFor(members[1]), out var mode).Ok, ref failures);
		Check("raid auto-response is rejected before preparation", !Join(1), ref failures);
		Check("preparation captures a nonzero generation", raids.TryBeginStart(members[0].UserId, out var raid2) && raid2.PreparationGeneration > 0, ref failures);
		Check("missing real party cannot start", !raids.IsPreparationReady(raid2, parties.ArePreparedRaidPartiesReady), ref failures);
		Check("wrong leader cannot authorize a raid party", !Respond(1, members[2].UserId, members[2].SessionId, members[1].SessionId), ref failures);
		Check("old leader session cannot authorize a raid party", !Respond(1, members[0].UserId, Guid.NewGuid(), members[1].SessionId), ref failures);
		Check("old member session cannot authorize a raid party", !Respond(1, members[0].UserId, members[0].SessionId, Guid.NewGuid()), ref failures);
		Check("failed preparation commit leaves response retryable", !raids.TryCommitPreparationResponse(members[0].UserId, members[0].SessionId, members[1].UserId, members[1].SessionId, (IReadOnlyList<RaidMember> _) => false), ref failures);
		int num;
		if (Join(1))
		{
			Party partyByUser = parties.GetPartyByUser(members[0].UserId);
			num = ((partyByUser != null && partyByUser.Count == 2) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		Check("first auto-response creates the real party", (byte)num != 0, ref failures);
		Check("partial party cannot start", !raids.IsPreparationReady(raid2, parties.ArePreparedRaidPartiesReady), ref failures);
		int num2;
		if (!Join(1))
		{
			Party partyByUser2 = parties.GetPartyByUser(members[0].UserId);
			num2 = ((partyByUser2 != null && partyByUser2.Count == 2) ? 1 : 0);
		}
		else
		{
			num2 = 0;
		}
		Check("duplicate auto-response is consumed only once", (byte)num2 != 0, ref failures);
		int num3;
		if (Join(2) && Join(3))
		{
			Party partyByUser3 = parties.GetPartyByUser(members[0].UserId);
			num3 = ((partyByUser3 != null && partyByUser3.Count == 4) ? 1 : 0);
		}
		else
		{
			num3 = 0;
		}
		Check("later auto-responses join the newly created party", (byte)num3 != 0, ref failures);
		Check("raid party settings match the current singleton client writer", parties.GetPartyByUser(members[0].UserId).PartyInfoBlock.SequenceEqual(new byte[12]
		{
			0, 0, 4, 255, 255, 255, 255, 5, 0, 2,
			0, 0
		}), ref failures);
		Check("complete real party permits start", raids.IsPreparationReady(raid2, parties.ArePreparedRaidPartiesReady), ref failures);
		Check("current preparation can cancel", raids.TryCancelPreparation(raid2, out raid), ref failures);
		Check("new preparation gets a different generation", raids.TryBeginStart(members[0].UserId, out var raid3) && raid3.PreparationGeneration != raid2.PreparationGeneration, ref failures);
		Check("old continuation cannot cancel or complete a new preparation", !raids.IsPreparationReady(raid2, parties.ArePreparedRaidPartiesReady) && !raids.TryCancelPreparation(raid2, out raid) && !raids.TryCompletePreparation(raid2, out raid), ref failures);
		int commits = 0;
		Check("stale preparation never invokes ticket commit", !raids.TryCompletePreparation(raid2, delegate
		{
			commits++;
			return true;
		}, out raid) && commits == 0, ref failures);
		Check("failed ticket commit leaves preparation pending", !raids.TryCompletePreparation(raid3, () => false, out raid) && raids.IsPreparationReady(raid3, parties.ArePreparedRaidPartiesReady), ref failures);
		Check("already formed matching raid party survives retry", raids.IsPreparationReady(raid3, parties.ArePreparedRaidPartiesReady), ref failures);
		raids.RebindSession(members[1].UserId, Guid.NewGuid());
		Check("reconnection invalidates the frozen preparation", !raids.IsPreparationReady(raid3, parties.ArePreparedRaidPartiesReady) && !Join(1) && !raids.TryCompletePreparation(raid3, out raid), ref failures);
		raids.TryCancelPreparation(raid3, out raid);
		raids.RebindSession(members[1].UserId, members[1].SessionId);
		raids.TryBeginStart(members[0].UserId, out var raid4);
		raids.TryAssignParty(members[0].UserId, members[3].UserId, 2u, out raid);
		Check("assignment change invalidates preparation", !raids.IsPreparationReady(raid4, parties.ArePreparedRaidPartiesReady) && !Join(1), ref failures);
		raids.TryCancelPreparation(raid4, out raid);
		raids.TryAssignParty(members[0].UserId, members[3].UserId, 1u, out raid);
		raids.TryBeginStart(members[0].UserId, out var raid5);
		Check("current complete preparation advances once", raids.IsPreparationReady(raid5, parties.ArePreparedRaidPartiesReady) && raids.TryCompletePreparation(raid5, delegate
		{
			commits++;
			return true;
		}, out var raid6) && raid6.State == 2 && !raids.TryCompletePreparation(raid5, delegate
		{
			commits++;
			return true;
		}, out raid) && commits == 1 && !Join(1), ref failures);
		PartyManager partyManager = new PartyManager();
		PartyMember partyMember = new PartyMember
		{
			UserId = 99,
			CharacterId = 99,
			SessionId = Guid.NewGuid()
		};
		Party party = partyManager.CreateParty(partyMember).Party;
		partyManager.Join(party.PartyId, PartyMemberFor(members[1]));
		Check("preparation cannot displace an unrelated party", !partyManager.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[1]), members).Ok && partyManager.GetPartyByUser(members[1].UserId)?.PartyId == party.PartyId && party.Count == 2, ref failures);
		PartyManager partyManager2 = new PartyManager();
		partyManager2.RecordInvite(members[1].UserId, members[1].SessionId, partyMember.UserId, partyMember.SessionId, out mode);
		Check("preparation cannot overwrite an ordinary invitation", !partyManager2.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[1]), members).Ok && partyManager2.CancelInvite(members[1].UserId, members[1].SessionId, partyMember.UserId, partyMember.SessionId) && partyManager2.PartyCount == 0, ref failures);
		PartyManager partyManager3 = new PartyManager();
		partyManager3.CreateParty(PartyMemberFor(members[0]));
		Check("ordinary default cannot bypass raid preparation", !partyManager3.ArePreparedRaidPartiesReady(members.Take(1).ToArray()), ref failures);
		partyManager3.UpdateSettings(members[0].UserId, members[0].SessionId, Array.Empty<byte>(), 4, new byte[12]
		{
			0, 5, 4, 0, 0, 0, 0, 5, 0, 0,
			255, 255
		});
		Check("explicit story solo settings are not raid-ready", !partyManager3.ArePreparedRaidPartiesReady(members.Take(1).ToArray()), ref failures);
		partyManager3.UpdateSettings(members[0].UserId, members[0].SessionId, Array.Empty<byte>(), 4, new byte[12]
		{
			0, 0, 4, 255, 255, 255, 255, 5, 0, 2,
			0, 0
		});
		Check("singleton SET_PARTY_INFO can complete preparation", partyManager3.ArePreparedRaidPartiesReady(members.Take(1).ToArray()), ref failures);
		PartyManager partyManager4 = new PartyManager();
		Party party2 = partyManager4.CreateParty(PartyMemberFor(members[0])).Party;
		foreach (RaidMember item2 in members.Skip(1))
		{
			partyManager4.Join(party2.PartyId, PartyMemberFor(item2));
		}
		PartyOpResult partyOpResult = partyManager4.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[1]), members);
		Check("matching full ordinary party is upgraded without removing members", partyOpResult.Ok && partyOpResult.MembershipUnchanged && partyOpResult.Party.PartyId == party2.PartyId && partyManager4.ArePreparedRaidPartiesReady(members) && partyManager4.PartyCount == 1, ref failures);
		RaidManager raidManager = new RaidManager();
		PartyManager multiParty = new PartyManager();
		RaidMember[] other = members.Select((RaidMember raidMember) => raidMember.Clone()).ToArray();
		other[0].PartyIndex = 2;
		other[3].PartyIndex = 2;
		RaidSnapshot raidSnapshot2 = raidManager.Create(Array.Empty<byte>(), other[0], 200);
		foreach (RaidMember item3 in other.Skip(1))
		{
			raidManager.TryAddMember(raidSnapshot2.RaidId, item3, out raid);
		}
		raidManager.TryBeginStart(other[0].UserId, out var raid7);
		Check("raid leader cannot accept members assigned to another small party", !raidManager.TryCommitPreparationResponse(other[0].UserId, other[0].SessionId, other[2].UserId, other[2].SessionId, (IReadOnlyList<RaidMember> _) => true), ref failures);
		Check("each small party uses its first listed member as leader", raidManager.TryCommitPreparationResponse(other[1].UserId, other[1].SessionId, other[2].UserId, other[2].SessionId, (IReadOnlyList<RaidMember> group) => multiParty.AcceptPreparedRaidMember(PartyMemberFor(other[1]), PartyMemberFor(other[2]), group).Ok) && multiParty.GetPartyByUser(other[2].UserId)?.LeaderUserId == other[1].UserId, ref failures);
		Check("all assigned small parties must finish, not just one", !raidManager.IsPreparationReady(raid7, multiParty.ArePreparedRaidPartiesReady), ref failures);
		raidManager.TryAddMember(raidSnapshot2.RaidId, new RaidMember
		{
			UserId = 100,
			CharacterId = 100u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 2
		}, out raid);
		Check("roster changes invalidate frozen preparation", !raidManager.TryCommitPreparationResponse(other[0].UserId, other[0].SessionId, other[3].UserId, other[3].SessionId, (IReadOnlyList<RaidMember> _) => true), ref failures);
		bool Join(int index)
		{
			return Respond(index, members[0].UserId, members[0].SessionId, members[index].SessionId);
		}
		static PartyMember PartyMemberFor(RaidMember raidMember)
		{
			return new PartyMember
			{
				UserId = raidMember.UserId,
				CharacterId = (int)raidMember.CharacterId,
				SessionId = raidMember.SessionId,
				Name = "raid-test"
			};
		}
		bool Respond(int index, ushort leader, Guid leaderSession, Guid memberSession)
		{
			return raids.TryCommitPreparationResponse(leader, leaderSession, members[index].UserId, memberSession, (IReadOnlyList<RaidMember> group) => parties.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[index]), group).Ok);
		}
	}

	private static void CheckRaidTimerRejoinAndTermination(ref int failures)
	{
		using var original = new RaidWireClient(96);
		using var rejoined = new RaidWireClient(96);
		using var rebound = new RaidWireClient(96);
		var raids = new RaidManager();
		var clock = new ClockService();
		var handler = new RaidHandler(
			DispatchProxy.Create<ICharacterRepository, UnusedDependency>(),
			new SessionDirectory(),
			raids,
			clock,
			AntonRaidTimerConfiguration.Create(RaidEtcFile.Parse(string.Empty), _ => { }));
		RaidSnapshot raid = raids.Create(new byte[] { 65 }, original.Member, 200);
		Guid version = handler.ScheduleRaidTimer(
			raid, 0u, 0u, "rejoin-test", 40u, true, 0,
			(_, _) => Task.CompletedTask);

		handler.HandleRejoinRaid(rejoined.Session, default, Array.Empty<byte>()).GetAwaiter().GetResult();
		byte[][] rejoinPackets = ReadPackets(rejoined, 6);
		uint firstRemaining = TimerRemaining(rejoinPackets);
		Thread.Sleep(1100);
		handler.HandleRebindResyncAsync(rebound.Session).GetAwaiter().GetResult();
		byte[][] rebindPackets = ReadPackets(rebound, 6);
		uint secondRemaining = TimerRemaining(rebindPackets);
		Check("rejoin and rebind replay one shared timer's decreasing deadline",
			firstRemaining is >= 39 and <= 40
			&& secondRemaining > 0
			&& secondRemaining < firstRemaining
			&& rejoinPackets.Count(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_REMAIN_TIME) == 1
			&& rebindPackets.Count(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_REMAIN_TIME) == 1
			&& handler.TimerCurrent(raid, 0u, 0u, "rejoin-test", version)
			&& clock.GetDebugSnapshot().OneShotTimers == 1,
			ref failures);
		Check("rebind does not project timer packets to the old sessions",
			original.Reader.Available == 0 && rejoined.Reader.Available == 0,
			ref failures);
		handler.CleanupRaidRuntimeState(raid);
		Check("rejoined raid timer is cancelled at instance cleanup",
			clock.GetDebugSnapshot().OneShotTimers == 0,
			ref failures);

		static uint TimerRemaining(byte[][] packets)
		{
			return BitConverter.ToUInt32(
				packets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_SET_TIMER),
				24);
		}

		static byte[][] ReadPackets(RaidWireClient client, int count)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			var packets = new byte[count][];
			for (int index = 0; index < count; index++)
			{
				byte[] header = new byte[15];
				client.Reader.GetStream().ReadExactlyAsync(header, timeout.Token).AsTask().GetAwaiter().GetResult();
				int length = checked((int)BitConverter.ToUInt32(header, 3));
				if (length < header.Length || length > 1048576)
					throw new InvalidOperationException("Invalid raid rejoin packet length");
				byte[] packet = new byte[length];
				header.CopyTo(packet, 0);
				client.Reader.GetStream().ReadExactlyAsync(packet.AsMemory(15), timeout.Token).AsTask().GetAwaiter().GetResult();
				packets[index] = packet;
			}
			return packets;
		}
	}

	private static void CheckPhaseIsolation(ref int failures)
	{
		RaidManager manager = new RaidManager();
		var timerClock = new ClockService();
		var timerManager = new RaidManager();
		var timerConfiguration = AntonRaidTimerConfiguration.Create(RaidEtcFile.Parse(string.Empty), _ => { });
		var raidHandler = new RaidHandler(
			DispatchProxy.Create<ICharacterRepository, UnusedDependency>(),
			DispatchProxy.Create<ISessionDirectory, UnusedDependency>(),
			timerManager,
			timerClock,
			timerConfiguration);
		RaidSnapshot timerRaid = timerManager.Create(
			new byte[] { 65 },
			new RaidMember
			{
				UserId = 90,
				CharacterId = 90,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1,
			},
			0);
		int timerCallbacks = 0;
		Guid guid = raidHandler.ScheduleRaidTimer(
			timerRaid, 9u, 9u, "selftest", 1u, false, null,
			(_, _) =>
			{
				Interlocked.Increment(ref timerCallbacks);
				return Task.CompletedTask;
			});
		Guid guid2 = raidHandler.ScheduleRaidTimer(
			timerRaid, 9u, 9u, "selftest", 1u, false, null,
			(_, _) =>
			{
				Interlocked.Increment(ref timerCallbacks);
				return Task.CompletedTask;
			});
		Check("timer replacement invalidates its previous callback",
			guid != guid2
			&& !raidHandler.TimerCurrent(timerRaid, 9u, 9u, "selftest", guid)
			&& raidHandler.TimerCurrent(timerRaid, 9u, 9u, "selftest", guid2),
			ref failures);
		timerClock.CheckOnce(DateTime.UtcNow.AddSeconds(2));
		Check("isolated clock runs only the current timer callback",
			SpinWait.SpinUntil(() => Volatile.Read(ref timerCallbacks) == 1, TimeSpan.FromSeconds(1)),
			ref failures);
		Guid cancelled = raidHandler.ScheduleRaidTimer(
			timerRaid, 8u, 8u, "cancel", 1u, false, null,
			(_, _) =>
			{
				Interlocked.Increment(ref timerCallbacks);
				return Task.CompletedTask;
			});
		raidHandler.CancelTimer(timerRaid, 8u, 8u);
		timerClock.CheckOnce(DateTime.UtcNow.AddSeconds(4));
		Check("cancel invalidates and exact-cancels the current registration",
			!raidHandler.TimerCurrent(timerRaid, 8u, 8u, "cancel", cancelled)
			&& Volatile.Read(ref timerCallbacks) == 1,
			ref failures);
		raidHandler.ScheduleRaidTimer(
			timerRaid, 6u, 6u, "projection", 30u, true, 0, (_, _) => Task.CompletedTask);
		DateTime snapshotStart = DateTime.UtcNow;
		IReadOnlyList<byte[]> initialTimerPackets = raidHandler.BuildRaidTimerSnapshotPackets(timerRaid, snapshotStart);
		IReadOnlyList<byte[]> laterTimerPackets = raidHandler.BuildRaidTimerSnapshotPackets(timerRaid, snapshotStart.AddSeconds(10));
		uint initialRemaining = BitConverter.ToUInt32(initialTimerPackets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_SET_TIMER), 24);
		uint laterRemaining = BitConverter.ToUInt32(laterTimerPackets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_SET_TIMER), 24);
		Check("rejoin timer projection keeps one absolute deadline",
			initialTimerPackets.Count == 2
			&& initialRemaining == 30
			&& laterRemaining == 20
			&& BitConverter.ToUInt32(laterTimerPackets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_REMAIN_TIME), 16) == laterRemaining,
			ref failures);
		IReadOnlyList<byte[]> expiredTimerPackets = raidHandler.BuildRaidTimerSnapshotPackets(timerRaid, snapshotStart.AddSeconds(31));
		Check("expired registration is never re-advertised at full duration",
			BitConverter.ToUInt32(expiredTimerPackets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_SET_TIMER), 24) == 0,
			ref failures);
		var wrongPhase = new RaidSnapshot
		{
			RaidId = timerRaid.RaidId,
			InstanceId = timerRaid.InstanceId,
			State = timerRaid.State,
			PhaseIndex = timerRaid.PhaseIndex + 1,
		};
		Guid wrongPhaseVersion = raidHandler.ScheduleRaidTimer(
			wrongPhase, 5u, 5u, "wrong-phase", 1u, false, null,
			(_, _) =>
			{
				Interlocked.Increment(ref timerCallbacks);
				return Task.CompletedTask;
			});
		var wrongInstance = new RaidSnapshot
		{
			RaidId = timerRaid.RaidId,
			InstanceId = Guid.NewGuid(),
			State = timerRaid.State,
			PhaseIndex = timerRaid.PhaseIndex,
		};
		Guid wrongInstanceVersion = raidHandler.ScheduleRaidTimer(
			wrongInstance, 5u, 6u, "wrong-instance", 1u, false, null,
			(_, _) =>
			{
				Interlocked.Increment(ref timerCallbacks);
				return Task.CompletedTask;
			});
		timerClock.CheckOnce(DateTime.UtcNow.AddSeconds(6));
		Check("old phase and old instance callbacks are no-ops",
			SpinWait.SpinUntil(
				() => !raidHandler.TimerCurrent(wrongPhase, 5u, 5u, "wrong-phase", wrongPhaseVersion)
					&& !raidHandler.TimerCurrent(wrongInstance, 5u, 6u, "wrong-instance", wrongInstanceVersion),
				TimeSpan.FromSeconds(1))
			&& Volatile.Read(ref timerCallbacks) == 1,
			ref failures);
		raidHandler.ScheduleRaidTimer(timerRaid, 7u, 7u, "cleanup-a", 30u, false, null, (_, _) => Task.CompletedTask);
		raidHandler.ScheduleRaidTimer(timerRaid, 7u, 8u, "cleanup-b", 30u, false, null, (_, _) => Task.CompletedTask);
		raidHandler.CleanupRaidRuntimeState(timerRaid);
		Check("raid instance cleanup cancels every prefixed timer",
			timerClock.GetDebugSnapshot().OneShotTimers == 0,
			ref failures);
		timerManager.Leave(timerRaid.LeaderUserId);
		RaidSnapshot replacementRaid = timerManager.Create(
			new byte[] { 66 },
			new RaidMember
			{
				UserId = 90,
				CharacterId = 90,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1,
			},
			0);
		raidHandler.ScheduleRaidTimer(timerRaid, 7u, 9u, "old-instance", 30u, false, null,
			(_, _) => Task.CompletedTask);
		Guid replacementVersion = raidHandler.ScheduleRaidTimer(
			replacementRaid, 7u, 9u, "new-instance", 30u, false, null,
			(_, _) => Task.CompletedTask);
		raidHandler.CleanupRaidRuntimeState(timerRaid);
		Check("old instance cleanup leaves same-id replacement timer alive",
			replacementRaid.RaidId == timerRaid.RaidId
			&& replacementRaid.InstanceId != timerRaid.InstanceId
			&& raidHandler.TimerCurrent(replacementRaid, 7u, 9u, "new-instance", replacementVersion)
			&& timerClock.GetDebugSnapshot().OneShotTimers == 1,
			ref failures);
		raidHandler.CleanupRaidRuntimeState(replacementRaid);
		RaidMember leader = new RaidMember
		{
			UserId = 42,
			CharacterId = 42u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidMember member = new RaidMember
		{
			UserId = 43,
			CharacterId = 43u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidSnapshot raidSnapshot = ToBreak();
		Check("nonleader cannot prepare phase two", !manager.TryPrepareNextPhase(member.UserId, out var raid), ref failures);
		Check("phase break timer can prepare phase two", manager.TryPrepareNextPhaseAutomatically(raidSnapshot, out var raid2), ref failures);
		Check("duplicate phase-two start is rejected", !manager.TryPrepareNextPhase(leader.UserId, out raid), ref failures);
		Check("phase two registers the normal small-party auto-response", manager.TryCommitPreparationResponse(leader.UserId, leader.SessionId, member.UserId, member.SessionId, (IReadOnlyList<RaidMember> group) => group.Count == 2 && group[0].UserId == leader.UserId), ref failures);
		Check("phase two does not bypass real-party readiness", !manager.IsPreparationReady(raid2, (IReadOnlyList<RaidMember> _) => false), ref failures);
		Check("failed phase-two preparation remains in standby", manager.TryCancelPreparation(raid2, out var raid3) && raid3.State == 5 && raid3.PhaseIndex == 0, ref failures);
		manager.TryPrepareNextPhase(leader.UserId, out var raid4);
		Check("old phase-two callback cannot complete or cancel retry", raid4.PreparationGeneration != raid2.PreparationGeneration && !manager.TryCompletePreparedNextPhase(raid2, (IReadOnlyList<RaidMember> _) => true, out raid) && !manager.TryCancelPreparation(raid2, out raid), ref failures);
		manager.RebindSession(member.UserId, Guid.NewGuid());
		Check("phase-two session replacement invalidates readiness and completion", !manager.IsPreparationReady(raid4, (IReadOnlyList<RaidMember> _) => true) && !manager.TryCompletePreparedNextPhase(raid4, (IReadOnlyList<RaidMember> _) => true, out raid), ref failures);
		manager.TryCancelPreparation(raid4, out raid);
		manager.RebindSession(member.UserId, member.SessionId);
		manager.TryPrepareNextPhase(leader.UserId, out var raid5);
		Check("phase transition rechecks real-party readiness", !manager.TryCompletePreparedNextPhase(raid5, (IReadOnlyList<RaidMember> _) => false, out raid), ref failures);
		Check("current phase two starts exactly once", manager.TryCompletePreparedNextPhase(raid5, (IReadOnlyList<RaidMember> _) => true, out var raid6) && raid6.State == 2 && raid6.PhaseIndex == 1 && raid6.StateArgument == 1 && !manager.TryCompletePreparedNextPhase(raid5, (IReadOnlyList<RaidMember> _) => true, out raid), ref failures);
		Check("phase-one timeout cannot fail phase two", !manager.TryFailPhase(raidSnapshot, out raid), ref failures);
		Check("current phase-two timeout fails and disbands it", manager.TryFailAndDisband(raid6, out var failed) && failed.State == 4 && failed.StateArgument == 1, ref failures);
		byte[] array = RaidHandler.BuildFailedRaidResultPacket(failed);
		Check("timeout and failed reconnect use result notification, not card-selection state", array.Length == 25 && array[0] == 0 && BitConverter.ToUInt16(array, 1) == 602 && array[15] == 1 && array[16] == 1 && array[24] == 1, ref failures);
		Check("timeout removes raid and every member mapping", !manager.TryGetByRaidId(raid6.RaidId, out raid) && !manager.TryGetByUser(leader.UserId, out raid) && !manager.TryGetByUser(member.UserId, out raid), ref failures);
		Check("failed phase cannot time out twice", !manager.TryFailAndDisband(raid6, out raid), ref failures);
		RaidSnapshot raidSnapshot2 = ToBreak();
		Check("same wire raid id has a new runtime instance", raidSnapshot2.RaidId == raidSnapshot.RaidId && raidSnapshot2.InstanceId != raidSnapshot.InstanceId, ref failures);
		Check("old break timer cannot prepare replacement raid", !manager.TryPrepareNextPhaseAutomatically(raidSnapshot, out raid), ref failures);
		manager.TryPrepareNextPhase(leader.UserId, out var raid7);
		Check("old ready callback cannot start or cancel replacement raid", !manager.TryCompletePreparedNextPhase(raid5, (IReadOnlyList<RaidMember> _) => true, out raid) && !manager.TryCancelPreparation(raid5, out raid), ref failures);
		manager.TryCompletePreparedNextPhase(raid7, (IReadOnlyList<RaidMember> _) => true, out var raid8);
		Check("old attack timeout cannot fail same-id replacement", !manager.TryFailPhase(raid6, out raid) && manager.TryGetByRaidId(raid8.RaidId, out var raid9) && raid9.State == 2, ref failures);
		Check("old delayed clear cannot finish same-id replacement", !manager.TryEnterPhaseBreak(raid6, out raid), ref failures);
		RaidSnapshot ToBreak()
		{
			RaidSnapshot raidSnapshot3 = manager.Create(Array.Empty<byte>(), leader, 200);
			manager.TryAddMember(raidSnapshot3.RaidId, member, out var raid10);
			if (!manager.TryBeginStart(leader.UserId, out var raid11) || !manager.TryCompletePreparation(raid11, out raid10) || !manager.TryEnterPhaseBreak(raidSnapshot3.RaidId, out raid10) || !manager.TryCompletePhase(raidSnapshot3.RaidId, out var raid12))
			{
				throw new InvalidOperationException("phase fixture");
			}
			return raid12;
		}
	}

	private static void CheckPhaseTwoLeaderOrder(ref int failures)
	{
		RaidManager raidManager = new RaidManager();
		PartyManager parties = new PartyManager();
		RaidMember raidMember = new RaidMember
		{
			UserId = 4,
			CharacterId = 4u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidMember b = new RaidMember
		{
			UserId = 5,
			CharacterId = 5u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidMember c = new RaidMember
		{
			UserId = 9,
			CharacterId = 9u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidSnapshot raidSnapshot = raidManager.Create(Array.Empty<byte>(), raidMember, 200);
		raidManager.TryAddMember(raidSnapshot.RaidId, b, out var raid);
		raidManager.TryAddMember(raidSnapshot.RaidId, c, out raid);
		RaidMember[] expected = new RaidMember[3] { raidMember, b, c };
		parties.CreateParty(P(raidMember));
		parties.AcceptPreparedRaidMember(P(raidMember), P(b), expected);
		parties.AcceptPreparedRaidMember(P(raidMember), P(c), expected);
		int partyId = parties.GetPartyByUser(4).PartyId;
		parties.TransferLeader(4, 5);
		raidManager.TryBeginStart(4, out var raid2);
		raidManager.TryCompletePreparation(raid2, out raid);
		raidManager.TryEnterPhaseBreak(raidSnapshot.RaidId, out raid);
		raidManager.TryCompletePhase(raidSnapshot.RaidId, out var raid3);
		Check("delegated leader blocks legacy ordering", !parties.ArePreparedRaidPartiesReady(raid3.Members), ref failures);
		Check("phase two resolves actual delegated leader", raidManager.TryPrepareNextPhase(4, out var raid4, parties.ResolveRaidPreparationOrder) && raid4.Members.Select((RaidMember m) => m.UserId).SequenceEqual(new ushort[3] { 5, 4, 9 }) && raid4.LeaderUserId == 4, ref failures);
		Check("same frozen roster passes real-party readiness", raidManager.IsPreparationReady(raid4, parties.ArePreparedRaidPartiesReady), ref failures);
		Check("old leader cannot authorize phase-two response", !raidManager.TryCommitPreparationResponse(4, raidMember.SessionId, 9, c.SessionId, (IReadOnlyList<RaidMember> _) => true), ref failures);
		Check("new leader reuses existing party", raidManager.TryCommitPreparationResponse(5, b.SessionId, 9, c.SessionId, (IReadOnlyList<RaidMember> group) => parties.AcceptPreparedRaidMember(P(b), P(c), group).Ok) && parties.GetPartyByUser(5).PartyId == partyId && parties.GetPartyByUser(5).Count == 3, ref failures);
		parties.TransferLeader(5, 4);
		Check("leader change during preparation blocks completion", !raidManager.TryCompletePreparedNextPhase(raid4, parties.ArePreparedRaidPartiesReady, out raid), ref failures);
		raidManager.TryCancelPreparation(raid4, out var raid5);
		Check("automatic retry resolves current leader too", raidManager.TryPrepareNextPhaseAutomatically(raid5, out var raid6, parties.ResolveRaidPreparationOrder) && raid6.Members[0].UserId == 4, ref failures);
		Check("old preparation cannot complete retry", !raidManager.TryCompletePreparedNextPhase(raid4, parties.ArePreparedRaidPartiesReady, out raid), ref failures);
		raidManager.TryCancelPreparation(raid6, out raid);
		raidManager.TryGetByRaidId(raidSnapshot.RaidId, out var raid7);
		Check("resolver cannot drop a member", !raidManager.TryPrepareNextPhase(4, out raid, (IReadOnlyList<RaidMember> m) => m.Take(2).ToArray()) && raidManager.TryGetByRaidId(raidSnapshot.RaidId, out var raid8) && raid8.AssignmentVersion == raid7.AssignmentVersion, ref failures);
		parties.Leave(9, c.SessionId);
		Check("missing real member rejects instead of rejoining", !raidManager.TryPrepareNextPhase(4, out raid, parties.ResolveRaidPreparationOrder), ref failures);
		static PartyMember P(RaidMember m)
		{
			return new PartyMember
			{
				UserId = m.UserId,
				CharacterId = (int)m.CharacterId,
				SessionId = m.SessionId
			};
		}
	}

	private static void CheckMemberClearCounts(ref int failures)
	{
		RaidManager raidManager = new RaidManager();
		RaidMember raidMember = new RaidMember
		{
			UserId = 42,
			CharacterId = 420u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1,
			NameBytes = new byte[2] { 196, 227 }
		};
		RaidMember member = new RaidMember
		{
			UserId = 43,
			CharacterId = 430u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidMember member2 = new RaidMember
		{
			UserId = 44,
			CharacterId = 440u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 2
		};
		RaidSnapshot raidSnapshot = raidManager.Create(Array.Empty<byte>(), raidMember, 200);
		raidManager.TryAddMember(raidSnapshot.RaidId, member, out var raid);
		raidManager.TryAddMember(raidSnapshot.RaidId, member2, out raid);
		raidManager.TryBeginStart(42, out var raid2);
		raidManager.TryCompletePreparation(raid2, out var raid3);
		Check("member counter enter succeeds", raidManager.TryEnterDungeon(42, 210u, out raid3, out var memberKeys), ref failures);
		Check("member counter clear succeeds", raidManager.TryClearDungeon(42, 210u, 4u, out raid3, out memberKeys, out var clearCount), ref failures);
		Check("only actual clearing squad increments", raid3.Members.Select((RaidMember m) => m.PhaseClearCount).SequenceEqual(new uint[3] { 1u, 1u, 0u }), ref failures);
		Check("duplicate squad callback rejected", !raidManager.TryClearDungeon(43, 210u, 4u, out raid, out memberKeys, out clearCount), ref failures);
		raidManager.TryGetByRaidId(raidSnapshot.RaidId, out raid3);
		Check("duplicate callback preserves counters", raid3.Members[0].PhaseClearCount == 1 && raid3.Members[1].PhaseClearCount == 1, ref failures);
		raidManager.ResetClearCounts(raidSnapshot.RaidId, new uint[1] { 210u });
		raidManager.TryGetByRaidId(raidSnapshot.RaidId, out raid3);
		Check("mechanic reset does not erase personal clears", raid3.Members[0].PhaseClearCount == 1, ref failures);
		byte[] array = RaidHandler.BuildRaidMemberDisplayName(raid3.Members[0], 2u);
		Check("raid display has no count prefix and preserves multibyte name", array.SequenceEqual(new byte[2] { 196, 227 }) && raidMember.NameBytes.SequenceEqual(new byte[2] { 196, 227 }), ref failures);
		RaidMemberSnapshot[] members = new RaidMemberSnapshot[1]
		{
			new RaidMemberSnapshot
			{
				UserId = 42,
				CharacterId = 420u,
				PhaseIndex = 1,
				PhaseClearCount = 258u,
				NameBytes = raidMember.NameBytes
			}
		};
		byte[] array2 = RaidPacketBuilder.BuildRaidMembersUpdate(1u, members, columnV1: false);
		byte[] source = RaidPacketBuilder.BuildRaidMembersUpdate(1u, members, columnV1: true);
		Check("column footer leaves every legacy byte unchanged", source.Take(array2.Length).SequenceEqual(array2), ref failures);
		Check("column footer carries version phase identity and full count", source.Skip(array2.Length).SequenceEqual(new byte[16]
		{
			82, 67, 67, 49, 1, 1, 42, 0, 164, 1,
			0, 0, 2, 1, 0, 0
		}), ref failures);
		Check("preparation display retains original name", RaidHandler.BuildRaidMemberDisplayName(raid3.Members[0], 0u).SequenceEqual(raidMember.NameBytes), ref failures);
		raidManager.TryAbandonDungeon(42, 210u, out raid, out memberKeys);
		raidManager.TryEnterDungeon(42, 210u, out raid, out memberKeys);
		Check("new dungeon run may increment again", raidManager.TryClearDungeon(42, 210u, 4u, out raid3, out memberKeys, out clearCount) && raid3.Members[0].PhaseClearCount == 2 && raid3.Members[1].PhaseClearCount == 2, ref failures);
		Check("earlier raid snapshot remains immutable", raidSnapshot.Members[0].PhaseClearCount == 0, ref failures);
		raidManager.TryEnterPhaseBreak(raidSnapshot.RaidId, out raid);
		raidManager.TryCompletePhase(raidSnapshot.RaidId, out raid);
		raidManager.TryPrepareNextPhase(42, out raid2);
		Check("phase two starts for counter reset", raidManager.TryCompletePreparedNextPhase(raid2, (IReadOnlyList<RaidMember> _) => true, out raid3), ref failures);
		Check("phase two clears all personal counters", raid3.Members.All((RaidMember m) => m.PhaseClearCount == 0), ref failures);
		RaidAggregate raidAggregate = new RaidAggregate(1u, Array.Empty<byte>(), raidMember);
		raidAggregate.RecordMemberClear(42);
		raidAggregate.RemoveMember(42);
		raidAggregate.AddMember(new RaidMember
		{
			UserId = 52,
			CharacterId = 420u
		});
		Check("same character rejoin retains phase count", raidAggregate.Snapshot().Members[0].PhaseClearCount == 1, ref failures);
		Check("new raid does not inherit phase count", new RaidAggregate(1u, Array.Empty<byte>(), raidMember).Snapshot().Members[0].PhaseClearCount == 0, ref failures);
	}

	private static void CheckMemberColumnRecipientIsolation(ref int failures)
	{
		EnhancedClientSession enhancedClientSession = new EnhancedClientSession(null, default(GamePacketHeader));
		EnhancedClientSession enhancedClientSession2 = new EnhancedClientSession(null, default(GamePacketHeader));
		EnhancedClientSession enhancedClientSession3 = new EnhancedClientSession(null, default(GamePacketHeader));
		EnhancedClientSession enhancedClientSession4 = new EnhancedClientSession(null, default(GamePacketHeader));
		PlayerContext player = enhancedClientSession2.Player;
		ushort num = (enhancedClientSession3.Player.UserId = 42);
		ushort userId = num;
		player.UserId = userId;
		PlayerContext player2 = enhancedClientSession2.Player;
		int num3 = (enhancedClientSession3.Player.CharacterId = 420);
		int characterId = num3;
		player2.CharacterId = characterId;
		string environmentVariable = Environment.GetEnvironmentVariable("A21_RAID_MEMBER_COLUMN_V1");
		try
		{
			Environment.SetEnvironmentVariable("A21_RAID_MEMBER_COLUMN_V1", "1");
			Check("RCC1 is off for a new session even with the old global switch set", !enhancedClientSession.SupportsRaidMemberColumnV1 && !enhancedClientSession4.SupportsRaidMemberColumnV1, ref failures);
			Check("only explicit supported version opts in this test session", !enhancedClientSession2.SupportsRaidMemberColumnV1 && enhancedClientSession2.TrySetRaidMemberColumnProtocolVersion(1u) && enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			Check("same-character reconnect does not inherit RCC1 capability", enhancedClientSession2.SupportsRaidMemberColumnV1 && !enhancedClientSession3.SupportsRaidMemberColumnV1 && enhancedClientSession2.SessionId != enhancedClientSession3.SessionId, ref failures);
			RaidMemberSnapshot raidMemberSnapshot = new RaidMemberSnapshot();
			raidMemberSnapshot.UserId = 42;
			raidMemberSnapshot.CharacterId = 420u;
			raidMemberSnapshot.PhaseIndex = 1;
			raidMemberSnapshot.PhaseClearCount = 258u;
			raidMemberSnapshot.NameBytes = new byte[2] { 196, 227 };
			RaidMemberSnapshot raidMemberSnapshot2 = raidMemberSnapshot;
			RaidMemberSnapshot[] rows = new RaidMemberSnapshot[1] { raidMemberSnapshot2 };
			RaidMember raidMember = new RaidMember
			{
				UserId = 42,
				CharacterId = 420u,
				NameBytes = raidMemberSnapshot2.NameBytes
			};
			RaidSnapshot raid = new RaidSnapshot
			{
				RaidId = 1u,
				TitleBytes = new byte[2] { 82, 49 },
				State = 2u,
				StateArgument = 1u,
				PhaseIndex = 1u,
				LeaderUserId = 42,
				Members = new RaidMember[1] { raidMember }
			};
			byte[] second = new byte[16]
			{
				82, 67, 67, 49, 1, 1, 42, 0, 164, 1,
				0, 0, 2, 1, 0, 0
			};
			byte[] array = RaidPacketBuilder.BuildRaidMembersUpdate(raid.RaidId, rows, columnV1: false);
			byte[] array2 = RaidPacketBuilder.BuildRaidMembersUpdate(raid.RaidId, rows, columnV1: true);
			byte[] array3 = RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raidMemberSnapshot2, rows, columnV1: false);
			byte[] array4 = RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raidMemberSnapshot2, rows, columnV1: true);
			Check("RCC1 isolation retains the frozen legacy op3 bytes", array.SequenceEqual(new byte[28]
			{
				1, 0, 0, 0, 3, 0, 0, 0, 1, 42,
				0, 1, 2, 0, 0, 0, 196, 227, 0, 0,
				0, 0, 164, 1, 0, 0, 0, 0
			}), ref failures);
			Check("op3 RCC1 is exactly the unchanged legacy body plus footer", array2.SequenceEqual(array.Concat(second)), ref failures);
			Check("op0 RCC1 is exactly the unchanged legacy body plus footer", array4.SequenceEqual(array3.Concat(second)), ref failures);
			Check("default builders ignore the retired global switch for op0 and op3", RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raidMemberSnapshot2, rows).SequenceEqual(array3) && RaidPacketBuilder.BuildRaidModify(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raidMemberSnapshot2, rows).SequenceEqual(array3) && RaidPacketBuilder.BuildRaidMembersUpdate(raid.RaidId, rows).SequenceEqual(array), ref failures);
			Check("modify op0 has the same explicit RCC1 choice as create", RaidPacketBuilder.BuildRaidModify(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raidMemberSnapshot2, rows, columnV1: true).SequenceEqual(array4), ref failures);
			EnhancedClientSession[] array5 = new EnhancedClientSession[4] { enhancedClientSession, enhancedClientSession2, enhancedClientSession3, enhancedClientSession4 };
			bool[] array6 = new bool[2] { true, false };
			foreach (bool flag in array6)
			{
				Dictionary<Guid, byte[]> delivered = new Dictionary<Guid, byte[]>();
				Func<EnhancedClientSession, byte[]> func = (flag ? ((Func<EnhancedClientSession, byte[]>)((EnhancedClientSession recipient) => RaidHandler.BuildRaidObjectPacketForRecipient(recipient, raid, rows))) : ((Func<EnhancedClientSession, byte[]>)((EnhancedClientSession recipient) => RaidHandler.BuildRaidMembersPacketForRecipient(recipient, raid.RaidId, rows))));
				RaidHandler.SendRaidPacketsByRecipientAsync(array5, func, delegate(EnhancedClientSession recipient, byte[] packet)
				{
					delivered.Add(recipient.SessionId, packet);
					return Task.CompletedTask;
				}).GetAwaiter().GetResult();
				byte[] expectedLegacy = GamePacketEnvelopeBuilder.Build(0, 592, flag ? array3 : array);
				byte[] expectedExtended = GamePacketEnvelopeBuilder.Build(0, 592, flag ? array4 : array2);
				Check("mixed recipients receive one correctly framed " + (flag ? "op0" : "op3") + " packet each", delivered.Count == array5.Length && array5.All((EnhancedClientSession recipient) => delivered[recipient.SessionId].SequenceEqual(recipient.SupportsRaidMemberColumnV1 ? expectedExtended : expectedLegacy)), ref failures);
				Check("direct create/view/recovery selector retains reconnect legacy " + (flag ? "op0" : "op3"), func(enhancedClientSession3).SequenceEqual(expectedLegacy) && func(enhancedClientSession2).SequenceEqual(expectedExtended), ref failures);
			}
			Check("unknown version rejects without revoking an already capable connection", !enhancedClientSession2.TrySetRaidMemberColumnProtocolVersion(2u) && enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			Check("overflow-sized version rejects without enabling a legacy connection", !enhancedClientSession.TrySetRaidMemberColumnProtocolVersion(uint.MaxValue) && !enhancedClientSession.SupportsRaidMemberColumnV1, ref failures);
			Check("version zero cannot enable or revoke capability", !enhancedClientSession.TrySetRaidMemberColumnProtocolVersion(0u) && !enhancedClientSession.SupportsRaidMemberColumnV1 && !enhancedClientSession2.TrySetRaidMemberColumnProtocolVersion(0u) && enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			Check("one-way capability preserves queued packet format selection", RaidHandler.BuildRaidMembersPacketForRecipient(enhancedClientSession2, raid.RaidId, rows).SequenceEqual(GamePacketEnvelopeBuilder.Build(0, 592, array2)), ref failures);
		}
		finally
		{
			Environment.SetEnvironmentVariable("A21_RAID_MEMBER_COLUMN_V1", environmentVariable);
			enhancedClientSession.Close();
			enhancedClientSession2.Close();
			enhancedClientSession3.Close();
			enhancedClientSession4.Close();
		}
	}

	private static void CheckMemberColumnBatchSend(ref int failures)
	{
		SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
		ConcurrentQueue<string> events;
		try
		{
			int writes = 0;
			bool prepared = false;
			sendLock.Wait();
			Task task = EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, delegate
			{
				prepared = true;
				return Array.Empty<byte[]>();
			}, delegate
			{
				writes++;
				return Task.CompletedTask;
			});
			Check("batch does not authorize or read a snapshot before acquiring send lock", !prepared && !task.IsCompleted, ref failures);
			sendLock.Release();
			task.GetAwaiter().GetResult();
			Check("empty rejected batch writes nothing and releases lock", prepared && writes == 0 && sendLock.CurrentCount == 1, ref failures);
			Check("prepare failure propagates and releases batch lock", Fails(EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, delegate
			{
				throw new InvalidOperationException("test prepare failure");
			}, (byte[] _) => Task.CompletedTask)) && sendLock.CurrentCount == 1, ref failures);
			Check("null prepared batch is rejected and releases lock", Fails(EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => (IReadOnlyList<byte[]>)null, (byte[] _) => Task.CompletedTask)) && sendLock.CurrentCount == 1, ref failures);
			writes = 0;
			Check("write failure stops the pair and releases lock without retrying", Fails(EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => new byte[2][]
			{
				new byte[1] { 2 },
				new byte[1] { 3 }
			}, delegate
			{
				writes++;
				return Task.FromException(new InvalidOperationException("test write failure"));
			})) && writes == 1 && sendLock.CurrentCount == 1, ref failures);
			events = new ConcurrentQueue<string>();
			TaskCompletionSource<bool> firstWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			TaskCompletionSource<bool> finishFirstWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			Task task2 = EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, delegate
			{
				events.Enqueue("prepare");
				return new byte[2][]
				{
					new byte[1] { 2 },
					new byte[1] { 3 }
				};
			}, async delegate(byte[] p)
			{
				events.Enqueue("op" + p[0]);
				if (p[0] == 2)
				{
					firstWrite.SetResult(result: true);
					await finishFirstWrite.Task;
				}
			});
			firstWrite.Task.GetAwaiter().GetResult();
			Task task3 = NormalSingleSend();
			Check("concurrent ordinary send waits while first response write is suspended", !task3.IsCompleted && events.SequenceEqual(new string[2] { "prepare", "op2" }), ref failures);
			finishFirstWrite.SetResult(result: true);
			Task.WhenAll(task2, task3).GetAwaiter().GetResult();
			Check("new handshake op2/op3 remain contiguous against ordinary send", events.SequenceEqual(new string[4] { "prepare", "op2", "op3", "broadcast" }) && sendLock.CurrentCount == 1, ref failures);
		}
		finally
		{
			if (sendLock != null)
			{
				((IDisposable)sendLock).Dispose();
			}
		}
		static bool Fails(Task task4)
		{
			try
			{
				task4.GetAwaiter().GetResult();
				return false;
			}
			catch (InvalidOperationException)
			{
				return true;
			}
		}
		Task NormalSingleSend()
		{
			return Send();
		}
		async Task Send()
		{
			await sendLock.WaitAsync();
			try
			{
				events.Enqueue("broadcast");
			}
			finally
			{
				sendLock.Release();
			}
		}
	}

	private static byte[] MemberColumnRequest(uint raidId, uint version = 1u, uint magic = 827343698u)
	{
		GamePacketWriter gamePacketWriter = new GamePacketWriter();
		gamePacketWriter.WriteUInt32(raidId);
		gamePacketWriter.WriteUInt32(magic);
		gamePacketWriter.WriteUInt32(version);
		return gamePacketWriter.ToArray();
	}

	private static EnhancedClientSession ColumnSession(ushort userId, int characterId, int port = 10200)
	{
		EnhancedClientSession enhancedClientSession = new EnhancedClientSession(null, default(GamePacketHeader), port);
		enhancedClientSession.Player.UserId = userId;
		enhancedClientSession.Player.CharacterId = characterId;
		return enhancedClientSession;
	}

	private static void CheckMemberColumnNegotiation(ref int failures)
	{
		SessionDirectory sessionDirectory = new SessionDirectory();
		RaidManager raidManager = new RaidManager();
		EnhancedClientSession enhancedClientSession = ColumnSession(42, 420);
		EnhancedClientSession enhancedClientSession2 = ColumnSession(77, 770);
		EnhancedClientSession enhancedClientSession3 = ColumnSession(78, 780);
		EnhancedClientSession enhancedClientSession4 = ColumnSession(79, 790);
		EnhancedClientSession enhancedClientSession5 = ColumnSession(80, 800, 10011);
		EnhancedClientSession enhancedClientSession6 = ColumnSession(77, 770);
		EnhancedClientSession[] array = new EnhancedClientSession[6] { enhancedClientSession, enhancedClientSession2, enhancedClientSession3, enhancedClientSession4, enhancedClientSession5, enhancedClientSession6 };
		try
		{
			foreach (EnhancedClientSession item in array.Take(5))
			{
				sessionDirectory.Register(item.Player.CharacterId, item);
			}
			RaidMember raidMember = new RaidMember
			{
				UserId = 42,
				CharacterId = 420u,
				SessionId = enhancedClientSession.SessionId,
				PartyIndex = 1,
				NameBytes = new byte[1] { 65 }
			};
			RaidSnapshot raid = raidManager.Create(Array.Empty<byte>(), raidMember, 200);
			RaidHandler raidHandler = new RaidHandler(DispatchProxy.Create<ICharacterRepository, UnusedDependency>(), sessionDirectory, raidManager);
			byte[] array2 = MemberColumnRequest(raid.RaidId);
			Check("RCP1 wire is exactly raid DWORD + RCP1 + DWORD version one", array2.Length == 12 && array2.Skip(4).SequenceEqual(new byte[8] { 82, 67, 80, 49, 1, 0, 0, 0 }) && RaidPacketBuilder.TryReadMemberColumnRequest(array2, out var raidId) && raidId == raid.RaidId, ref failures);
			byte[] bytes = BitConverter.GetBytes(raid.RaidId);
			Check("four-byte request is not a capability declaration", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession2, bytes, out var raid2, out var packets) && packets.Count == 0 && !enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			List<byte[]> list = new List<byte[]>
			{
				null,
				Array.Empty<byte>(),
				MemberColumnRequest(raid.RaidId, 0u),
				MemberColumnRequest(raid.RaidId, 2u),
				MemberColumnRequest(raid.RaidId, uint.MaxValue),
				MemberColumnRequest(raid.RaidId, 1u, 0u),
				MemberColumnRequest(0u)
			};
			int[] array3 = new int[7] { 1, 3, 5, 8, 11, 13, 16 };
			foreach (int num in array3)
			{
				list.Add(array2.Take(num).Concat(new byte[Math.Max(0, num - array2.Length)]).ToArray());
			}
			foreach (byte[] item2 in list)
			{
				Check("malformed/unknown RCP1 has no response and cannot opt in", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession2, item2, out raid2, out packets) && packets.Count == 0 && !enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			}
			RaidSnapshot raidSnapshot = raidManager.Create(Array.Empty<byte>(), new RaidMember
			{
				UserId = 90,
				CharacterId = 900u,
				SessionId = Guid.NewGuid()
			}, 201);
			Check("RCP1 rejects an existing cross-channel raid", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession4, MemberColumnRequest(raidSnapshot.RaidId), out raid2, out packets) && packets.Count == 0 && !enhancedClientSession4.SupportsRaidMemberColumnV1, ref failures);
			Check("RCP1 rejects an absent same-channel raid", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession4, MemberColumnRequest(13172734u), out raid2, out packets) && packets.Count == 0 && !enhancedClientSession4.SupportsRaidMemberColumnV1, ref failures);
			Check("RCP1 rejects a non-raid listener", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession5, array2, out raid2, out packets) && packets.Count == 0 && !enhancedClientSession5.SupportsRaidMemberColumnV1, ref failures);
			Check("RCP1 rejects an unregistered connection", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession6, array2, out raid2, out packets) && packets.Count == 0 && !enhancedClientSession6.SupportsRaidMemberColumnV1, ref failures);
			Check("same-channel nonmember may explicitly opt into a read-only raid view", raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession2, array2, out var raid3, out var packets2) && raid3.RaidId == raid.RaidId && enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			Check("RCP1 responds only with 592 op2 then op3, never 588 or status replay", packets2.Count == 2 && packets2.All((byte[] p) => p[0] == 0 && BitConverter.ToUInt16(p, 1) == 592) && BitConverter.ToUInt32(packets2[0], 19) == 2 && BitConverter.ToUInt32(packets2[1], 19) == 3, ref failures);
			Check("RCP1 info preserves original state and contains no new footer", packets2[0].SequenceEqual(GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidInfoUpdate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, new RaidMemberSnapshot
			{
				UserId = 42,
				CharacterId = 420u,
				PartyIndex = 1,
				NameBytes = new byte[1] { 65 }
			}))), ref failures);
			for (int num2 = 0; num2 < 5; num2++)
			{
				Check("repeat RCP1 is idempotent and returns the same two snapshot packets", raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession2, array2, out raid2, out var packets3) && packets3.Count == 2 && packets3.Zip(packets2).All(((byte[] First, byte[] Second) pair) => pair.First.SequenceEqual(pair.Second)), ref failures);
			}
			raidManager.TryGetByRaidId(raid.RaidId, out var raid4);
			Check("viewing does not join/rebind/mutate raid counts or assignment", !raidManager.TryGetByUser(enhancedClientSession2.Player.UserId, out raid2) && raid4.InstanceId == raid.InstanceId && raid4.AssignmentVersion == raid.AssignmentVersion && raid4.Members.Count == 1 && raid4.Leader.SessionId == enhancedClientSession.SessionId && raid4.Leader.PhaseClearCount == 0, ref failures);
			Check("unknown request after opt-in sends nothing and never downgrades queued RCC1", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession2, MemberColumnRequest(raid.RaidId, 2u), out raid2, out packets) && packets.Count == 0 && enhancedClientSession2.SupportsRaidMemberColumnV1, ref failures);
			RaidMemberSnapshot row = new RaidMemberSnapshot
			{
				UserId = 42,
				CharacterId = 420u,
				PartyIndex = 1,
				NameBytes = new byte[1] { 65 }
			};
			Dictionary<Guid, byte[]> delivered = new Dictionary<Guid, byte[]>();
			RaidHandler.SendRaidPacketsByRecipientAsync(new EnhancedClientSession[2] { enhancedClientSession2, enhancedClientSession3 }, (EnhancedClientSession s) => RaidHandler.BuildRaidMembersPacketForRecipient(s, raid.RaidId, new RaidMemberSnapshot[1] { row }), delegate(EnhancedClientSession s, byte[] p)
			{
				delivered[s.SessionId] = p;
				return Task.CompletedTask;
			}).GetAwaiter().GetResult();
			Check("production negotiation drives mixed-recipient automatic refresh formatting", delivered[enhancedClientSession2.SessionId].SequenceEqual(packets2[1]) && delivered[enhancedClientSession3.SessionId].SequenceEqual(GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidMembersUpdate(raid.RaidId, new RaidMemberSnapshot[1] { row }, columnV1: false))), ref failures);
			sessionDirectory.Register(enhancedClientSession6.Player.CharacterId, enhancedClientSession6);
			Check("replacement session starts off and the displaced connection cannot declare", !enhancedClientSession6.SupportsRaidMemberColumnV1 && !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession2, array2, out raid2, out packets) && packets.Count == 0, ref failures);
			Check("replacement session needs its own valid declaration", raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession6, array2, out raid2, out var _) && enhancedClientSession6.SupportsRaidMemberColumnV1, ref failures);
			raidManager.Leave(raidMember.UserId);
			Check("last-member removal/empty raid is not a stale view target", !raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession4, array2, out raid2, out packets) && packets.Count == 0 && !enhancedClientSession4.SupportsRaidMemberColumnV1, ref failures);
		}
		finally
		{
			EnhancedClientSession[] array4 = array;
			for (int num3 = 0; num3 < array4.Length; num3++)
			{
				array4[num3].Close();
			}
		}
	}

	private static void CheckMemberColumnTerminalStates(ref int failures)
	{
		uint[] array = new uint[2] { 0u, 1u };
		foreach (uint num in array)
		{
			bool[] array2 = new bool[2] { false, true };
			foreach (bool num2 in array2)
			{
				RaidManager raidManager = new RaidManager();
				RaidSnapshot raid = raidManager.Create(leader: new RaidMember
				{
					UserId = 42,
					CharacterId = 420u,
					SessionId = Guid.NewGuid(),
					PartyIndex = 1
				}, titleBytes: Array.Empty<byte>(), channelId: 200);
				raidManager.TryBeginStart(42, out var raid2);
				raidManager.TryCompletePreparation(raid2, out raid);
				RaidSnapshot raid3;
				if (num == 1)
				{
					raidManager.TryEnterPhaseBreak(raid.RaidId, out raid3);
					raidManager.TryCompletePhase(raid.RaidId, out raid3);
					raidManager.TryPrepareNextPhase(42, out raid2);
					Check("terminal fixture enters real phase two", raidManager.TryCompletePreparedNextPhase(raid2, (IReadOnlyList<RaidMember> _) => true, out raid), ref failures);
				}
				uint dungeonId = ((num == 0) ? 210u : 211u);
				raidManager.TryEnterDungeon(42, dungeonId, out raid3, out var memberKeys);
				raidManager.TryClearDungeon(42, dungeonId, 4u, out raid, out memberKeys, out var _);
				if (num2)
				{
					Check($"real phase {num} failure keeps its own phase and result flag", raidManager.TryFailPhase(raid, out raid) && raid.State == 4 && raid.StateArgument == 1 && raid.PhaseIndex == num, ref failures);
				}
				else
				{
					raidManager.TryEnterPhaseBreak(raid.RaidId, out raid);
					CheckMemberColumnSnapshot(raid, ref failures);
					Check($"real phase {num} completion keeps its own phase and success flag", raidManager.TryCompletePhase(raid.RaidId, out raid) && raid.StateArgument == 0 && raid.PhaseIndex == num, ref failures);
				}
				CheckMemberColumnSnapshot(raid, ref failures);
				SessionDirectory sessionDirectory = new SessionDirectory();
				EnhancedClientSession enhancedClientSession = ColumnSession(77, 770);
				sessionDirectory.Register(770, enhancedClientSession);
				RaidHandler raidHandler = new RaidHandler(DispatchProxy.Create<ICharacterRepository, UnusedDependency>(), sessionDirectory, raidManager);
				Check($"terminal phase {num} RCP1 view uses op2 plus truthful op3", raidHandler.TryPrepareRaidMemberColumnView(enhancedClientSession, MemberColumnRequest(raid.RaidId), out raid3, out var packets) && packets.Count == 2 && BitConverter.ToUInt32(packets[0], 19) == 2 && BitConverter.ToUInt32(packets[1], 19) == 3 && packets[1][packets[1].Length - 12] == num && BitConverter.ToUInt32(packets[1], packets[1].Length - 4) == 1, ref failures);
				enhancedClientSession.Close();
			}
		}
	}

	private static void CheckMemberColumnSnapshot(RaidSnapshot raid, ref int failures)
	{
		EnhancedClientSession enhancedClientSession = ColumnSession(77, 770);
		EnhancedClientSession enhancedClientSession2 = ColumnSession(78, 780);
		enhancedClientSession.TrySetRaidMemberColumnProtocolVersion(1u);
		RaidMemberSnapshot leader = new RaidMemberSnapshot
		{
			UserId = raid.Leader.UserId,
			CharacterId = raid.Leader.CharacterId,
			PartyIndex = raid.Leader.PartyIndex,
			NameBytes = raid.Leader.NameBytes
		};
		bool[] array = new bool[2] { false, true };
		foreach (bool flag in array)
		{
			RaidMemberSnapshot[] rows = (flag ? Array.Empty<RaidMemberSnapshot>() : raid.Members.Select((RaidMember m) => new RaidMemberSnapshot
			{
				UserId = m.UserId,
				CharacterId = m.CharacterId,
				PartyIndex = m.PartyIndex,
				NameBytes = m.NameBytes,
				PhaseIndex = (byte)raid.PhaseIndex,
				PhaseClearCount = m.PhaseClearCount
			}).ToArray());
			byte[] array2 = RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, leader, rows, columnV1: false);
			byte[] array3 = RaidHandler.BuildRaidObjectPacketForRecipient(enhancedClientSession, raid, rows);
			bool flag2 = raid.StateArgument != raid.PhaseIndex;
			Check($"state {raid.State}/{raid.StateArgument} phase {raid.PhaseIndex} empty={flag} op0 preserves legacy state/body and only valid footer", array3.Skip(15).Take(array2.Length).SequenceEqual(array2) && array3.Length == 15 + array2.Length + ((!flag2) ? (6 + 10 * rows.Length) : 0), ref failures);
			List<byte[]> delivered = new List<byte[]>();
			RaidHandler.SendRaidPacketsByRecipientAsync(new EnhancedClientSession[2] { enhancedClientSession, enhancedClientSession2 }, (EnhancedClientSession s) => RaidHandler.BuildRaidObjectPacketForRecipient(s, raid, rows), delegate(EnhancedClientSession _, byte[] p)
			{
				delivered.Add(p);
				return Task.CompletedTask;
			}).GetAwaiter().GetResult();
			Check("terminal op0 cannot throw and cancel mixed-recipient fanout", delivered.Count == 2 && delivered[1].SequenceEqual(GamePacketEnvelopeBuilder.Build(0, 592, array2)), ref failures);
			byte[] array4 = RaidHandler.BuildRaidMembersPacketForRecipient(enhancedClientSession, raid.RaidId, rows);
			int num = array4.Length - 6 - 10 * rows.Length;
			Check("op3 retains actual phase/count; non-null empty roster is a clear", BitConverter.ToUInt32(array4, num) == 826491730 && array4[num + 4] == ((!flag) ? raid.PhaseIndex : 0) && array4[num + 5] == rows.Length, ref failures);
		}
		enhancedClientSession.Close();
		enhancedClientSession2.Close();
	}

	private static void CheckMemberColumnFooterPhases(ref int failures)
	{
		byte[] array = new byte[2] { 0, 1 };
		foreach (byte phase in array)
		{
			RaidMemberSnapshot leader = new RaidMemberSnapshot
			{
				UserId = 1,
				CharacterId = 100u,
				PhaseIndex = phase
			};
			int[] array2 = new int[3] { 0, 1, 20 };
			foreach (int num in array2)
			{
				RaidMemberSnapshot[] rows = (from num2 in Enumerable.Range(0, num)
					select new RaidMemberSnapshot
					{
						UserId = (ushort)(num2 + 1),
						CharacterId = (uint)(100 + num2),
						PhaseIndex = phase,
						PhaseClearCount = (uint)(258 + num2)
					}).ToArray();
				byte[] array3 = RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, phase, leader, rows, columnV1: false);
				byte[] extended = RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, phase, leader, rows, columnV1: true);
				int footer = array3.Length;
				bool condition = extended.Length == footer + 6 + 10 * num && extended.Take(footer).SequenceEqual(array3) && BitConverter.ToUInt32(extended, footer) == 826491730 && extended[footer + 4] == phase && extended[footer + 5] == num && rows.Select((RaidMemberSnapshot row, int num2) => BitConverter.ToUInt16(extended, footer + 6 + 10 * num2) == row.UserId && BitConverter.ToUInt32(extended, footer + 8 + 10 * num2) == row.CharacterId && BitConverter.ToUInt32(extended, footer + 12 + 10 * num2) == row.PhaseClearCount).All((bool value) => value);
				Check($"op0 RCC1 object phase {phase} is retained for {num} members without changing legacy bytes", condition, ref failures);
				Check($"op0 modify uses the same explicit phase {phase} for {num} members", RaidPacketBuilder.BuildRaidModify(1u, Array.Empty<byte>(), 2u, phase, leader, rows, columnV1: true).SequenceEqual(extended), ref failures);
				if (num == 0)
				{
					continue;
				}
				rows[num - 1].PhaseIndex = (byte)(1 - phase);
				Check($"op0 phase {phase} rejects an inconsistent member in a {num}-member roster", RejectsArgument(() => RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, phase, leader, rows, columnV1: true)), ref failures);
				Check($"legacy op0 ignores footer-only member phase for {num} members", RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, phase, leader, rows, columnV1: false).SequenceEqual(array3), ref failures);
				if (num > 1)
				{
					Check("op3 RCC1 rejects mixed member phases", RejectsArgument(() => RaidPacketBuilder.BuildRaidMembersUpdate(1u, rows, columnV1: true)), ref failures);
				}
			}
		}
		RaidMemberSnapshot legacyLeader = new RaidMemberSnapshot
		{
			UserId = 1,
			CharacterId = 100u
		};
		uint[] array4 = new uint[2] { 2u, 255u };
		foreach (uint stateArgument in array4)
		{
			byte[] array5 = RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, stateArgument, legacyLeader, Array.Empty<RaidMemberSnapshot>(), columnV1: false);
			Check($"legacy op0 still preserves StateArgument {stateArgument}", array5[18] == stateArgument && RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, stateArgument, legacyLeader, Array.Empty<RaidMemberSnapshot>()).SequenceEqual(array5), ref failures);
			Check($"RCC1 alone rejects unsupported object phase {stateArgument} even for an empty roster", RejectsRange(() => RaidPacketBuilder.BuildRaidCreate(1u, Array.Empty<byte>(), 2u, stateArgument, legacyLeader, Array.Empty<RaidMemberSnapshot>(), columnV1: true)), ref failures);
		}
		byte[] first = RaidPacketBuilder.BuildRaidMembersUpdate(1u, Array.Empty<RaidMemberSnapshot>(), columnV1: false);
		byte[] first2 = RaidPacketBuilder.BuildRaidMembersUpdate(1u, Array.Empty<RaidMemberSnapshot>(), columnV1: true);
		Check("empty op3 RCC1 is an explicit count-zero clear with phase-zero placeholder", first2.SequenceEqual(first.Concat(new byte[6] { 82, 67, 67, 49, 0, 0 })), ref failures);
		static bool RejectsArgument(Func<byte[]> build)
		{
			try
			{
				build();
				return false;
			}
			catch (ArgumentException)
			{
				return true;
			}
		}
	}

	private static void CheckRaidTownReturn(ref int failures)
	{
		RaidManager raidManager = new RaidManager();
		RaidMember raidMember = new RaidMember
		{
			UserId = 42,
			CharacterId = 42u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidSnapshot raidSnapshot = raidManager.Create(Array.Empty<byte>(), raidMember, 200);
		Check("raid return rejected before completion", !raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out var raid), ref failures);
		raidManager.TryBeginStart(42, out var raid2);
		raidManager.TryCompletePreparation(raid2, out raid);
		raidManager.TryEnterDungeon(42, 210u, out raid, out var memberKeys);
		raidManager.TryClearDungeon(42, 210u, 4u, out raid, out memberKeys, out var _);
		raidManager.TryEnterPhaseBreak(raidSnapshot.RaidId, out raid);
		Check("raid return rejected during reward selection", !raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out raid), ref failures);
		raidManager.TryCompletePhase(raidSnapshot.RaidId, out var raid3);
		Check("completed participant may replay town UI", raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out raid), ref failures);
		Check("repeat replay is read-only and allowed", raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out var raid4) && raid4.InstanceId == raid3.InstanceId && raid4.State == 5, ref failures);
		Check("wrong session cannot replay town", !raidManager.TryGetCompletedTownReturn(42, Guid.NewGuid(), out raid), ref failures);
		PlayerContext playerContext = new PlayerContext
		{
			CharacterId = 42,
			UserId = 42,
			CurTownId = 19,
			TownPresenceReady = true,
			UserState = 0
		};
		Check("town UI replay requires valid current member", TownHandler.IsCompletedRaidTownReturn(10200, playerContext, raidMember.SessionId, raid3), ref failures);
		Check("ordinary channel cannot replay raid town", !TownHandler.IsCompletedRaidTownReturn(10010, playerContext, raidMember.SessionId, raid3), ref failures);
		playerContext.UserState = 1;
		Check("non-town player cannot replay town", !TownHandler.IsCompletedRaidTownReturn(10200, playerContext, raidMember.SessionId, raid3), ref failures);
		playerContext.UserState = 0;
		playerContext.CharacterId = 43;
		Check("switched character cannot replay town", !TownHandler.IsCompletedRaidTownReturn(10200, playerContext, raidMember.SessionId, raid3), ref failures);
		raidManager.TryPrepareNextPhase(42, out var raid5);
		Check("second phase preparation blocks old reward return", !raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out raid), ref failures);
		raidManager.TryCompletePreparedNextPhase(raid5, (IReadOnlyList<RaidMember> _) => true, out raid);
		Check("second phase combat blocks old reward return", !raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out raid), ref failures);
		RaidSnapshot raidSnapshot2 = raidManager.Create(Array.Empty<byte>(), raidMember, 200);
		Check("same-id replacement cannot inherit reward return", raidSnapshot2.InstanceId != raid3.InstanceId && !raidManager.TryGetCompletedTownReturn(42, raidMember.SessionId, out raid), ref failures);
	}

	private static void CheckSituationWire(ref int failures)
	{
		Check("symbol update uses byte count and intact key/value", RaidPacketBuilder.BuildSetSymbol(50u, 2u).SequenceEqual(new byte[9] { 1, 50, 0, 0, 0, 2, 0, 0, 0 }), ref failures);
		KeyValuePair<uint, uint>[] symbols = (from i in Enumerable.Range(0, 255)
			select new KeyValuePair<uint, uint>((uint)i, (uint)(i + 1))).ToArray();
		byte[] body = RaidPacketBuilder.BuildSetSymbols(symbols);
		Check("255 symbols retain all pair boundaries", body.Length == 2041 && body[0] == byte.MaxValue && Enumerable.Range(0, 255).All((int i) => BitConverter.ToUInt32(body, 1 + 8 * i) == i && BitConverter.ToUInt32(body, 5 + 8 * i) == i + 1), ref failures);
		Check("empty symbols have one-byte count", RaidPacketBuilder.BuildSetSymbols(Array.Empty<KeyValuePair<uint, uint>>()).SequenceEqual(new byte[1] { 0 }), ref failures);
		uint[] array = new uint[4] { 0u, 1u, 2u, 4u };
		foreach (uint num2 in array)
		{
			string name = $"participation operation {num2} compact record";
			ReadOnlySpan<byte> span = RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210u, num2, new uint[2] { 5u, 513u });
			byte[] array2 = new byte[11]
			{
				1, 210, 0, 0, 0, 0, 2, 5, 0, 1,
				2
			};
			array2[5] = (byte)num2;
			Check(name, span.SequenceEqual(array2), ref failures);
		}
		Check("empty participation members preserve header", RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210u, 0u, Array.Empty<uint>()).Length == 7, ref failures);
		Check("four member participation is fifteen bytes", RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210u, 1u, new uint[4] { 4u, 5u, 9u, 65535u }).Length == 15, ref failures);
		Check("symbol count overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildSetSymbols(symbols.Concat(symbols.Take(1)).ToArray());
		}), ref failures);
		Check("participation operation overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210u, 256u, new uint[1] { 5u });
		}), ref failures);
		Check("participation member overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210u, 1u, new uint[1] { 65536u });
		}), ref failures);
		Check("participation count overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210u, 1u, Enumerable.Repeat(5u, 256).ToArray());
		}), ref failures);
		RaidSituationGroup[] groups = new RaidSituationGroup[4]
		{
			new RaidSituationGroup
			{
				PartyIndex = 1,
				DungeonId = 210u,
				MemberKeys = new uint[2] { 4u, 5u }
			},
			new RaidSituationGroup
			{
				PartyIndex = 2,
				DungeonId = 213u,
				MemberKeys = new uint[1] { 9u },
				DungeonCleared = true
			},
			new RaidSituationGroup
			{
				PartyIndex = 3,
				DungeonId = 0u,
				MemberKeys = new uint[1] { 10u }
			},
			new RaidSituationGroup
			{
				PartyIndex = 4,
				DungeonId = 215u,
				MemberKeys = Array.Empty<uint>()
			}
		};
		IReadOnlyList<byte[]> readOnlyList = RaidHandler.BuildRaidParticipationRefreshPackets(groups);
		Check("refresh includes other parties, skips town/empty groups and restores cleared flag", readOnlyList.Count == 5 && readOnlyList.Select((byte[] p) => p[20]).SequenceEqual(new byte[5] { 0, 2, 0, 2, 4 }) && readOnlyList.All((byte[] p) => BitConverter.ToUInt16(p, 1) == 585) && BitConverter.ToUInt32(readOnlyList[0], 16) == 210 && BitConverter.ToUInt32(readOnlyList[2], 16) == 213, ref failures);
		Check("repeated refresh has deterministic remove-before-add sequence", readOnlyList.Zip(RaidHandler.BuildRaidParticipationRefreshPackets(groups), (byte[] a, byte[] b) => a.SequenceEqual(b)).All((bool v) => v), ref failures);
		Check("empty raid refresh sends no fabricated occupancy", RaidHandler.BuildRaidParticipationRefreshPackets(Array.Empty<RaidSituationGroup>()).Count == 0, ref failures);
		static bool Overflow(Action action)
		{
			try
			{
				action();
				return false;
			}
			catch (OverflowException)
			{
				return true;
			}
		}
	}

	private static void CheckRewardList(ref int failures)
	{
		RaidRewardEntry row = new RaidRewardEntry
		{
			UserId = 513,
			CardType = 3,
			Flags = 4u,
			ItemId = 305419896u,
			Quantity = 258u
		};
		uint[] array = new uint[4] { 0u, 1u, 2u, 3u };
		foreach (uint num in array)
		{
			byte[] array2 = RaidPacketBuilder.BuildRaidRewardList(num, new RaidRewardEntry[1] { row });
			string name = $"reward type {num} exact A21 layout";
			ReadOnlySpan<byte> span = array2;
			byte[] array3 = new byte[12]
			{
				0, 1, 1, 2, 3, 4, 120, 86, 52, 18,
				2, 1
			};
			array3[0] = (byte)num;
			Check(name, span.SequenceEqual(array3), ref failures);
		}
		int[] array4 = new int[4] { 0, 4, 20, 255 };
		foreach (int num2 in array4)
		{
			RaidRewardEntry[] rewards = (from num3 in Enumerable.Range(0, num2)
				select new RaidRewardEntry
				{
					UserId = (ushort)(num3 + 1),
					CardType = 1,
					ItemId = (uint)(440116 + num3),
					Quantity = 1u
				}).ToArray();
			byte[] body2 = RaidPacketBuilder.BuildRaidRewardList(3u, rewards);
			Check($"squad reward {num2} members preserves every record boundary", body2.Length == 2 + 10 * num2 && body2[0] == 3 && body2[1] == num2 && Enumerable.Range(0, num2).All((int num3) => BitConverter.ToUInt16(body2, 2 + 10 * num3) == num3 + 1 && BitConverter.ToUInt32(body2, 6 + 10 * num3) == 440116 + num3 && BitConverter.ToUInt16(body2, 10 + 10 * num3) == 1), ref failures);
		}
		byte[] array5 = GamePacketEnvelopeBuilder.Build(0, 601, RaidPacketBuilder.BuildRaidRewardList(3u, new RaidRewardEntry[1] { row }));
		Check("reward notification uses A21 enum and compact body", array5.Length == 27 && BitConverter.ToUInt16(array5, 1) == 601 && array5[15] == 3 && array5[16] == 1, ref failures);
		Check("reward type overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidRewardList(256u, new RaidRewardEntry[1] { row });
		}), ref failures);
		Check("reward count overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidRewardList(3u, Enumerable.Repeat(row, 256).ToArray());
		}), ref failures);
		row.Flags = 256u;
		Check("reward flag overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidRewardList(3u, new RaidRewardEntry[1] { row });
		}), ref failures);
		row.Flags = 0u;
		row.Quantity = 65536u;
		Check("reward quantity overflow rejected", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidRewardList(3u, new RaidRewardEntry[1] { row });
		}), ref failures);
		row.Quantity = 65535u;
		Check("maximum reward quantity preserved", BitConverter.ToUInt16(RaidPacketBuilder.BuildRaidRewardList(3u, new RaidRewardEntry[1] { row }), 10) == ushort.MaxValue, ref failures);
		Check("native gold quantity cannot silently truncate 120000 into u16", Overflow(delegate
		{
			RaidPacketBuilder.BuildRaidRewardList(0u, new RaidRewardEntry[1] { RaidHandler.BuildPhaseOnePartyCardRevealEntry(4, 0, 0u, 120000) });
		}), ref failures);
		Check("party material reveal preserves actual quantity", RaidHandler.GetPhaseOnePartyCardDisplayCount(1, 120) == 120 && RaidHandler.GetPhaseOnePartyCardDisplayItemId(1, 10094735u, 3330u) == 3330, ref failures);
		static bool Overflow(Action build)
		{
			try
			{
				build();
				return false;
			}
			catch (OverflowException)
			{
				return true;
			}
		}
	}

	private static void Check(string name, bool condition, ref int failures)
	{
		Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + name);
		if (!condition)
		{
			failures++;
		}
	}

	private static bool RejectsRange(Func<byte[]> build)
	{
		try
		{
			build();
			return false;
		}
		catch (ArgumentOutOfRangeException)
		{
			return true;
		}
	}

	private static void CheckRaidDepartureNotifications(ref int failures)
	{
		string[] array = new string[9] { "member-leave", "leader-leave", "last-leave", "member-disconnect", "leader-disconnect", "timeout-0", "timeout-1", "broken-leader-leave", "broken-timeout" };
		foreach (string text in array)
		{
			try
			{
				using RaidWireClient raidWireClient = new RaidWireClient(41);
				using RaidWireClient raidWireClient2 = new RaidWireClient(42);
				using RaidWireClient raidWireClient3 = new RaidWireClient(43);
				using RaidWireClient raidWireClient4 = new RaidWireClient(44, 10201);
				using RaidWireClient raidWireClient5 = new RaidWireClient(45, 10011);
				using RaidWireClient raidWireClient6 = new RaidWireClient(46);
				SessionDirectory sessionDirectory = new SessionDirectory();
				RaidWireClient[] array2 = new RaidWireClient[5] { raidWireClient, raidWireClient2, raidWireClient3, raidWireClient4, raidWireClient5 };
				foreach (RaidWireClient raidWireClient7 in array2)
				{
					sessionDirectory.Register(raidWireClient7.Session.Player.CharacterId, raidWireClient7.Session);
				}
				raidWireClient6.Session.TcpClient.Dispose();
				sessionDirectory.Register(46, raidWireClient6.Session);
				RaidManager raidManager = new RaidManager();
				RaidSnapshot raid = raidManager.Create(new byte[1] { 65 }, raidWireClient.Member, 200);
				if (text != "last-leave")
				{
					raidManager.TryAddMember(raid.RaidId, raidWireClient2.Member, out raid);
				}
				var timerClock = new ClockService();
				RaidHandler raidHandler = new RaidHandler(
					DispatchProxy.Create<ICharacterRepository, UnusedDependency>(),
					sessionDirectory,
					raidManager,
					timerClock,
					AntonRaidTimerConfiguration.Create(RaidEtcFile.Parse(string.Empty), _ => { }));
				Guid sharedTimerVersion = raidHandler.ScheduleRaidTimer(
					raid, 12u, 12u, "departure-test", 3600u, false, null,
					(_, _) => Task.CompletedTask);
				bool flag = text.StartsWith("member-", StringComparison.Ordinal);
				bool flag2 = text.Contains("timeout", StringComparison.Ordinal);
				bool flag3 = text.StartsWith("broken-", StringComparison.Ordinal);
				RaidWireClient raidWireClient8 = (flag ? raidWireClient2 : raidWireClient);
				if (flag3)
				{
					raidWireClient.Session.TcpClient.Dispose();
				}
				if (flag2)
				{
					raidManager.TryBeginStart(41, out var raid2);
					raidManager.TryCompletePreparation(raid2, out raid);
					if (text == "timeout-1")
					{
						raidManager.TryEnterPhaseBreak(raid.RaidId, out raid);
						raidManager.TryCompletePhase(raid.RaidId, out raid);
						raidManager.TryPrepareNextPhase(41, out raid2);
						raidManager.TryCompletePreparedNextPhase(raid2, (IReadOnlyList<RaidMember> _) => true, out raid);
					}
					Guid guid = raidHandler.ScheduleRaidTimer(
						raid,
						0u,
						0u,
						"attack",
						3600u,
						false,
						null,
						(_, _) => Task.CompletedTask);
					((Task)typeof(RaidHandler).GetMethod("RunAttackTimeoutAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(raidHandler, new object[2] { raid, guid })).GetAwaiter().GetResult();
				}
				else if (text.EndsWith("disconnect", StringComparison.Ordinal))
				{
					raidHandler.ClearSessionAsync(raidWireClient8.Session.SessionId).GetAwaiter().GetResult();
				}
				else
				{
					try
					{
						raidHandler.HandleLeaveRaid(raidWireClient8.Session, new GamePacketHeader
						{
							type = 666
						}, Array.Empty<byte>()).GetAwaiter().GetResult();
					}
					catch (ObjectDisposedException) when (flag3)
					{
					}
				}
				List<byte[]> list = raidWireClient3.ReadThroughDirectory();
				byte[] array3 = list.Last();
				Check(text + " observer receives authoritative directory", BitConverter.ToUInt32(array3, 15) == (flag ? 1u : 0u) && (!flag || array3.Last() == 1), ref failures);
				Check(text + " observer receives detail before directory", list.Count == 2 && BitConverter.ToUInt16(list[0], 1) == 592 && BitConverter.ToUInt32(list[0], 15) == raid.RaidId && BitConverter.ToUInt32(list[0], 19) == (uint)((!flag) ? 1 : 3), ref failures);
				Check(text + " server membership agrees with list", raidManager.TryGetByRaidId(raid.RaidId, out var raid3) == flag && (!flag || raid3.Members.Count == 1), ref failures);
				Check(text + " shared timer obeys raid-instance lifetime",
					flag
						? raidHandler.TimerCurrent(raid, 12u, 12u, "departure-test", sharedTimerVersion)
							&& timerClock.GetDebugSnapshot().OneShotTimers == 1
						: !raidHandler.TimerCurrent(raid, 12u, 12u, "departure-test", sharedTimerVersion)
							&& timerClock.GetDebugSnapshot().OneShotTimers == 0,
					ref failures);
				List<byte[]> list2 = (flag ? raidWireClient : raidWireClient2).ReadThroughDirectory();
				Check(text + " remaining or former member receives same directory", list2.Last().SequenceEqual(array3), ref failures);
				if (flag2)
				{
					string name = text + " healthy member receives failure before removal";
					int condition;
					if (list2.Any((byte[] p) => BitConverter.ToUInt16(p, 1) == 602 && p[15] == 1 && p[24] == 1))
					{
						condition = ((BitConverter.ToUInt16(list2[list2.Count - 2], 1) == 592) ? 1 : 0);
					}
					else
					{
						condition = 0;
					}
					Check(name, (byte)condition != 0, ref failures);
				}
				if (!flag3)
				{
					Check(text + " departing client receives same directory", raidWireClient8.ReadThroughDirectory().Last().SequenceEqual(array3), ref failures);
				}
				Check(text + " other channels receive no packets", raidWireClient4.Reader.Available == 0 && raidWireClient5.Reader.Available == 0, ref failures);
				raidHandler.ClearSessionAsync(raidWireClient8.Session.SessionId).GetAwaiter().GetResult();
				Check(text + " repeated cleanup is idempotent", raidWireClient3.Reader.Available == 0, ref failures);
			}
			catch (Exception ex2)
			{
				Console.WriteLine(text + ": " + ex2);
				Check(text + " wire regression completed", condition: false, ref failures);
			}
		}
	}

	private static byte[] RaidPeerReply(ushort requester, uint result = 0u)
	{
		return BitConverter.GetBytes(requester).Concat(new byte[1] { 10 }).Concat(BitConverter.GetBytes(result))
			.ToArray();
	}

	private static void CheckRaidJoinConfirmation(ref int failures)
	{
		Check("native raid confirmation accepts seven-byte zero response", PartyHandler.IsAcceptedRaidPeerResponse(RaidPeerReply(72)), ref failures);
		byte[][] array = new byte[7][]
		{
			null,
			Array.Empty<byte>(),
			new byte[6],
			new byte[8],
			RaidPeerReply(72, 1u),
			RaidPeerReply(72, uint.MaxValue),
			RaidPeerReply(72).Concat(new byte[2] { 133, 0 }).ToArray()
		};
		foreach (byte[] body in array)
		{
			Check("raid reject malformed and nonzero responses cannot confirm", !PartyHandler.IsAcceptedRaidPeerResponse(body), ref failures);
		}
		RaidSnapshot raid;
		using (RaidJoinFixture raidJoinFixture = new RaidJoinFixture())
		{
			raidJoinFixture.Applicant.Session.Player.CurAreaId = 2;
			raidJoinFixture.Apply();
			byte[] value = ReadManagerAck(raidJoinFixture.Leader);
			byte[] array2 = ReadManagerAck(raidJoinFixture.Leader);
			Check("application supplies requester context before native type10 popup", BitConverter.ToUInt16(value, 1) == 2 && BitConverter.ToUInt16(value, 56) == 72 && BitConverter.ToUInt16(array2, 1) == 7 && array2.Length == 28 && BitConverter.ToUInt16(array2, 15) == 72 && array2[17] == 10, ref failures);
			Check("application alone never adds member or sends join packets", !raidJoinFixture.Raids.TryGetByUser(72, out raid) && raidJoinFixture.Applicant.Reader.Available == 0, ref failures);
			raidJoinFixture.Respond(raidJoinFixture.Applicant, 71);
			raidJoinFixture.Respond(raidJoinFixture.Third, 72);
			Check("applicant automatic channel reply and unrelated third party cannot approve application", !raidJoinFixture.Raids.TryGetByUser(72, out raid), ref failures);
			raidJoinFixture.Respond(raidJoinFixture.Leader, 72);
			Check("intended leader confirmation joins original raid", raidJoinFixture.Raids.TryGetByUser(72, out var raid2) && raid2.InstanceId == raidJoinFixture.Original.InstanceId && raid2.Members.Count == 2, ref failures);
			List<byte[]> list = raidJoinFixture.Applicant.ReadThroughDirectory();
			int condition;
			if (list.Any((byte[] p) => BitConverter.ToUInt16(p, 1) == 592))
			{
				condition = ((list[list.Count - 1].Last() == 2) ? 1 : 0);
			}
			else
			{
				condition = 0;
			}
			Check("confirmed join publishes raid state and updated directory", (byte)condition != 0, ref failures);
			raidJoinFixture.Respond(raidJoinFixture.Leader, 72);
			Check("replayed confirmation does not add a second member", raidJoinFixture.Raids.TryGetByUser(72, out raid2) && raid2.Members.Count == 2, ref failures);
		}
		string[] array3 = new string[14]
		{
			"reject", "nonzero", "expiry", "duplicate-expiry", "recreated", "transfer", "transfer-back", "start", "already-joined", "same-account",
			"recipient-loading", "requester-loading", "recipient-reconnect", "requester-reconnect"
		};
		foreach (string text in array3)
		{
			using RaidJoinFixture raidJoinFixture2 = new RaidJoinFixture();
			raidJoinFixture2.Apply();
			ReadManagerAck(raidJoinFixture2.Leader);
			ReadManagerAck(raidJoinFixture2.Leader);
			byte[] array4 = RaidPeerReply(72);
			RaidWireClient raidWireClient = null;
			try
			{
				switch (text)
				{
				case "reject":
					array4 = array4.Concat(new byte[2] { 133, 0 }).ToArray();
					break;
				case "nonzero":
					array4 = RaidPeerReply(72, 123u);
					break;
				case "expiry":
					raidJoinFixture2.Now = 60000L;
					break;
				case "duplicate-expiry":
					raidJoinFixture2.Now = 59000L;
					raidJoinFixture2.Apply();
					Check("duplicate application does not resend popup or renew pending lifetime", raidJoinFixture2.Leader.Reader.Available == 0, ref failures);
					raidJoinFixture2.Now = 60000L;
					break;
				case "recreated":
					raidJoinFixture2.Raids.Create(new byte[1] { 66 }, raidJoinFixture2.Leader.Member, 200);
					break;
				case "transfer":
				case "transfer-back":
					raidJoinFixture2.Raids.TryAddMember(raidJoinFixture2.Original.RaidId, raidJoinFixture2.Third.Member, out raid);
					raidJoinFixture2.Raids.TryTransferLeadership(71, raidJoinFixture2.Leader.Session.SessionId, 73, raidJoinFixture2.Third.Session.SessionId, out raid);
					if (text == "transfer-back")
					{
						raidJoinFixture2.Raids.TryTransferLeadership(73, raidJoinFixture2.Third.Session.SessionId, 71, raidJoinFixture2.Leader.Session.SessionId, out raid);
					}
					break;
				case "start":
					raidJoinFixture2.Raids.TryBeginStart(71, out raid);
					break;
				case "already-joined":
					raidJoinFixture2.Raids.Create(new byte[1] { 67 }, raidJoinFixture2.Applicant.Member, 200);
					break;
				case "same-account":
					((RaidJoinCharacters)raidJoinFixture2.Characters).SameAccount = true;
					break;
				case "recipient-loading":
					raidJoinFixture2.Leader.Session.Player.TownPresenceReady = false;
					break;
				case "requester-loading":
					raidJoinFixture2.Applicant.Session.Player.TownPresenceReady = false;
					break;
				case "recipient-reconnect":
				case "requester-reconnect":
					raidWireClient = new RaidWireClient((ushort)((text == "recipient-reconnect") ? 71 : 72));
					RaidJoinFixture.Ready(raidWireClient);
					raidJoinFixture2.Sessions.Register(raidWireClient.Session.Player.CharacterId, raidWireClient.Session);
					break;
				}
				raidJoinFixture2.Respond((text == "recipient-reconnect") ? raidWireClient : raidJoinFixture2.Leader, 72, array4);
				Check(text + " cannot use old approval to join original raid", !raidJoinFixture2.Raids.TryGetByUser(72, out var raid3) || raid3.InstanceId != raidJoinFixture2.Original.InstanceId, ref failures);
				if (text == "already-joined")
				{
					Check("delayed approval never removes existing raid membership", raidJoinFixture2.Raids.TryGetByUser(72, out raid3) && raid3.LeaderUserId == 72, ref failures);
				}
				switch (text)
				{
				case "reject":
				case "nonzero":
				case "expiry":
					raidJoinFixture2.Respond(raidJoinFixture2.Leader, 72);
					Check(text + " consumes pending request so replay cannot confirm", !raidJoinFixture2.Raids.TryGetByUser(72, out raid), ref failures);
					break;
				}
			}
			finally
			{
				raidWireClient?.Dispose();
			}
		}
		using (RaidJoinFixture raidJoinFixture3 = new RaidJoinFixture())
		{
			Check("leader invitation records a confirmation for invited player", raidJoinFixture3.Peers.RequestRaidPeerAsync(raidJoinFixture3.Leader.Session, raidJoinFixture3.Applicant.Session, 999).GetAwaiter().GetResult(), ref failures);
			ReadManagerAck(raidJoinFixture3.Applicant);
			ReadManagerAck(raidJoinFixture3.Applicant);
			raidJoinFixture3.Respond(raidJoinFixture3.Leader, 72);
			Check("inviter cannot approve their own invitation", !raidJoinFixture3.Raids.TryGetByUser(72, out raid), ref failures);
			raidJoinFixture3.Respond(raidJoinFixture3.Applicant, 71);
			Check("invited player zero response joins without echoing peerInt", raidJoinFixture3.Raids.TryGetByUser(72, out raid), ref failures);
		}
		using (RaidJoinFixture raidJoinFixture4 = new RaidJoinFixture())
		{
			raidJoinFixture4.Peers.RequestRaidPeerAsync(raidJoinFixture4.Leader.Session, raidJoinFixture4.Applicant.Session, 0).GetAwaiter().GetResult();
			ReadManagerAck(raidJoinFixture4.Applicant);
			ReadManagerAck(raidJoinFixture4.Applicant);
			raidJoinFixture4.Apply();
			ReadManagerAck(raidJoinFixture4.Leader);
			ReadManagerAck(raidJoinFixture4.Leader);
			raidJoinFixture4.Respond(raidJoinFixture4.Applicant, 71);
			Check("new application cancels old reverse invitation and automatic reply", !raidJoinFixture4.Raids.TryGetByUser(72, out raid), ref failures);
			raidJoinFixture4.Respond(raidJoinFixture4.Leader, 72);
			Check("superseding application still requires and accepts leader confirmation", raidJoinFixture4.Raids.TryGetByUser(72, out raid), ref failures);
		}
		using (RaidJoinFixture raidJoinFixture5 = new RaidJoinFixture())
		{
			raidJoinFixture5.Raids.TryAddMember(raidJoinFixture5.Original.RaidId, raidJoinFixture5.Third.Member, out raid);
			Check("ordinary raid member cannot issue a leader invitation", !raidJoinFixture5.Peers.RequestRaidPeerAsync(raidJoinFixture5.Third.Session, raidJoinFixture5.Applicant.Session, 0).GetAwaiter().GetResult(), ref failures);
			Check("ordinary raid member cannot receive admission applications", !raidJoinFixture5.Peers.RequestRaidPeerAsync(raidJoinFixture5.Applicant.Session, raidJoinFixture5.Third.Session, 0).GetAwaiter().GetResult(), ref failures);
			raidJoinFixture5.Respond(raidJoinFixture5.Third, 72);
			Check("nonleader unsolicited acceptance cannot admit applicant", !raidJoinFixture5.Raids.TryGetByUser(72, out raid) && raidJoinFixture5.Applicant.Reader.Available == 0, ref failures);
		}
		using (RaidJoinFixture raidJoinFixture6 = new RaidJoinFixture())
		{
			raidJoinFixture6.Apply();
			ReadManagerAck(raidJoinFixture6.Leader);
			ReadManagerAck(raidJoinFixture6.Leader);
			raidJoinFixture6.Raids.TryAddMember(raidJoinFixture6.Original.RaidId, raidJoinFixture6.Third.Member, out raid);
			raidJoinFixture6.Respond(raidJoinFixture6.Leader, 72);
			Check("other admissions do not invalidate pending leader approvals", raidJoinFixture6.Raids.TryGetByUser(72, out var raid4) && raid4.Members.Count == 3, ref failures);
		}
		using (RaidJoinFixture raidJoinFixture7 = new RaidJoinFixture())
		{
			raidJoinFixture7.Apply();
			ReadManagerAck(raidJoinFixture7.Leader);
			ReadManagerAck(raidJoinFixture7.Leader);
			raidJoinFixture7.Raids.Create(new byte[1] { 66 }, raidJoinFixture7.Third.Member, 200);
			raidJoinFixture7.Peers.RequestRaidPeerAsync(raidJoinFixture7.Applicant.Session, raidJoinFixture7.Third.Session, 0).GetAwaiter().GetResult();
			ReadManagerAck(raidJoinFixture7.Third);
			ReadManagerAck(raidJoinFixture7.Third);
			raidJoinFixture7.Respond(raidJoinFixture7.Leader, 72);
			Check("new raid application invalidates former raid approval", !raidJoinFixture7.Raids.TryGetByUser(72, out raid), ref failures);
			raidJoinFixture7.Respond(raidJoinFixture7.Third, 72);
			Check("only new intended leader can admit the applicant", raidJoinFixture7.Raids.TryGetByUser(72, out var raid5) && raid5.LeaderUserId == 73, ref failures);
		}
		using (RaidJoinFixture raidJoinFixture8 = new RaidJoinFixture())
		{
			raidJoinFixture8.Leader.Session.TcpClient.Dispose();
			Check("failed popup delivery cancels the application", !raidJoinFixture8.Peers.RequestRaidPeerAsync(raidJoinFixture8.Applicant.Session, raidJoinFixture8.Leader.Session, 0).GetAwaiter().GetResult(), ref failures);
			raidJoinFixture8.Respond(raidJoinFixture8.Leader, 72);
			Check("failed delivery cannot later be accepted", !raidJoinFixture8.Raids.TryGetByUser(72, out raid), ref failures);
		}
		using (RaidJoinFixture raidJoinFixture9 = new RaidJoinFixture())
		{
			raidJoinFixture9.Apply();
			for (ushort num = 100; num < 119; num++)
			{
				raidJoinFixture9.Raids.TryAddMember(raidJoinFixture9.Original.RaidId, new RaidMember
				{
					UserId = num,
					CharacterId = num,
					SessionId = Guid.NewGuid()
				}, out raid);
			}
			raidJoinFixture9.Respond(raidJoinFixture9.Leader, 72);
			Check("raid filled while popup open rejects late approval", !raidJoinFixture9.Raids.TryGetByUser(72, out raid) && raidJoinFixture9.Raids.TryGetByUser(71, out var raid6) && raid6.Members.Count == 20, ref failures);
		}
		RaidJoinFixture f = new RaidJoinFixture();
		try
		{
			RaidSnapshot other = f.Raids.Create(new byte[1] { 66 }, f.Third.Member, 200);
			bool first = false;
			bool second = false;
			Parallel.Invoke(delegate
			{
				first = f.Raids.TryAddConfirmedMember(f.Original, f.Applicant.Member, out var _);
			}, delegate
			{
				second = f.Raids.TryAddConfirmedMember(other, f.Applicant.Member, out var _);
			});
			Check("concurrent approval atomically admits character to exactly one raid", first != second && f.Raids.TryGetByUser(72, out raid), ref failures);
			Check("competing approval preserves both raids", f.Raids.TryGetByUser(71, out var raid7) && f.Raids.TryGetByUser(73, out var raid8) && raid7.Members.Count + raid8.Members.Count == 3, ref failures);
		}
		finally
		{
			if (f != null)
			{
				((IDisposable)f).Dispose();
			}
		}
	}

	private static byte[] RaidManagerCommand(uint operation, uint target, uint argument = 0u)
	{
		return BitConverter.GetBytes(operation).Concat(BitConverter.GetBytes(target)).Concat(BitConverter.GetBytes(argument))
			.ToArray();
	}

	private static ushort ReadRaidObjectLeader(byte[] packet, int objectOffset)
	{
		int num = checked((int)BitConverter.ToUInt32(packet, objectOffset + 4));
		return BitConverter.ToUInt16(packet, objectOffset + 15 + num);
	}

	private static byte[] ReadManagerAck(RaidWireClient client)
	{
		client.Reader.ReceiveTimeout = 5000;
		byte[] array = new byte[15];
		client.Reader.GetStream().ReadExactly(array);
		byte[] array2 = new byte[checked((int)BitConverter.ToUInt32(array, 3))];
		array.CopyTo(array2, 0);
		client.Reader.GetStream().ReadExactly(array2.AsSpan(15));
		return array2;
	}

	private static void CheckRaidLeaderTransfer(ref int failures)
	{
		Check("native leader-transfer request is three DWORDs op1 target and zero", RaidHandler.TryReadRaidManagerWork(RaidManagerCommand(1u, 62u), out var operation, out var targetUserId, out var partyIndex) && operation == 1 && targetUserId == 62 && partyIndex == 0, ref failures);
		Check("native party assignment still uses op0 and party index", RaidHandler.TryReadRaidManagerWork(RaidManagerCommand(0u, 62u, 10u), out operation, out targetUserId, out partyIndex) && partyIndex == 10, ref failures);
		byte[][] array = new byte[10][]
		{
			null,
			Array.Empty<byte>(),
			new byte[8],
			new byte[11],
			new byte[13],
			RaidManagerCommand(1u, 0u),
			RaidManagerCommand(1u, 65598u),
			RaidManagerCommand(2u, 62u),
			RaidManagerCommand(1u, 62u, 1u),
			RaidManagerCommand(0u, 62u, 11u)
		};
		foreach (byte[] body in array)
		{
			Check("invalid manager request cannot alias a valid target or operation", !RaidHandler.TryReadRaidManagerWork(body, out var _, out var _, out var _), ref failures);
		}
		uint[] array2 = new uint[3] { 0u, 2u, 5u };
		foreach (uint num in array2)
		{
			long elapsed = 0L;
			RaidManager raidManager = new RaidManager(() => elapsed);
			RaidMember raidMember = new RaidMember
			{
				UserId = 61,
				CharacterId = 61u,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1
			};
			RaidMember raidMember2 = new RaidMember
			{
				UserId = 62,
				CharacterId = 62u,
				SessionId = Guid.NewGuid(),
				PartyIndex = 2
			};
			RaidMember raidMember3 = new RaidMember
			{
				UserId = 63,
				CharacterId = 63u,
				SessionId = Guid.NewGuid(),
				PartyIndex = 1
			};
			RaidSnapshot raid = raidManager.Create(new byte[1] { 65 }, raidMember, 200);
			raidManager.TryAddMember(raid.RaidId, raidMember2, out raid);
			raidManager.TryAddMember(raid.RaidId, raidMember3, out raid);
			RaidSnapshot raid3;
			if (num != 0)
			{
				raidManager.TryBeginStart(61, out var raid2);
				Check("leadership cannot change inside frozen start preparation", !raidManager.TryTransferLeadership(61, raidMember.SessionId, 62, raidMember2.SessionId, out raid3), ref failures);
				raidManager.TryCompletePreparation(raid2, out raid);
				elapsed = 12000L;
				if (num == 5)
				{
					raidManager.TryEnterPhaseBreak(raid.RaidId, out raid3);
					raidManager.TryCompletePhase(raid.RaidId, out raid);
				}
			}
			Check($"state {num} rejects a nonleader transfer", !raidManager.TryTransferLeadership(63, raidMember3.SessionId, 62, raidMember2.SessionId, out raid3), ref failures);
			Check($"state {num} rejects stale actor and target sessions", !raidManager.TryTransferLeadership(61, Guid.NewGuid(), 62, raidMember2.SessionId, out raid3) && !raidManager.TryTransferLeadership(61, raidMember.SessionId, 62, Guid.NewGuid(), out raid3), ref failures);
			Check($"state {num} rejects outsiders and self transfer", !raidManager.TryTransferLeadership(61, raidMember.SessionId, 99, Guid.NewGuid(), out raid3) && !raidManager.TryTransferLeadership(61, raidMember.SessionId, 61, raidMember.SessionId, out raid3), ref failures);
			Check($"state {num} leader transfer updates authoritative identity", raidManager.TryTransferLeadership(61, raidMember.SessionId, 62, raidMember2.SessionId, out var raid4) && raid4.LeaderUserId == 62 && raid4.Leader.UserId == 62, ref failures);
			Check($"state {num} transfer preserves raid instance phase roster and squads", raid4.RaidId == raid.RaidId && raid4.InstanceId == raid.InstanceId && raid4.State == raid.State && raid4.PhaseIndex == raid.PhaseIndex && raid4.PhaseClearTimeSeconds == raid.PhaseClearTimeSeconds && raid4.Members.Select((RaidMember m) => (UserId: m.UserId, PartyIndex: m.PartyIndex)).SequenceEqual(raid.Members.Select((RaidMember m) => (UserId: m.UserId, PartyIndex: m.PartyIndex))) && raid4.AssignmentVersion != raid.AssignmentVersion, ref failures);
			Check($"state {num} old snapshot remains immutable", raid.LeaderUserId == 61, ref failures);
			Check($"state {num} former leader loses assignment and transfer authority", !raidManager.TryAssignParty(61, 63, 2u, out raid3) && !raidManager.TryTransferLeadership(61, raidMember.SessionId, 63, raidMember3.SessionId, out raid3), ref failures);
			Check($"state {num} commander chat follows new leadership", !ChatHandler.CanSendRaidMessage(53, 61, raid4.LeaderUserId) && ChatHandler.CanSendRaidMessage(53, 62, raid4.LeaderUserId), ref failures);
			if (num == 0)
			{
				Check("only new leader may start the raid after transfer", !raidManager.TryBeginStart(61, out raid3) && raidManager.TryBeginStart(62, out raid3), ref failures);
				raidManager.TryCancelStart(raid4.RaidId, 62, out raid3);
			}
			if (num == 5)
			{
				Check("only new leader may prepare the second phase", !raidManager.TryPrepareNextPhase(61, out raid3) && raidManager.TryPrepareNextPhase(62, out raid3), ref failures);
				raidManager.TryCancelStart(raid4.RaidId, 62, out raid3);
			}
			RaidLeaveResult raidLeaveResult = raidManager.Leave(61);
			Check($"state {num} former leader leaving keeps the raid alive", raidLeaveResult.Ok && !raidLeaveResult.Disbanded && raidLeaveResult.RemainingRaid.LeaderUserId == 62 && raidLeaveResult.RemainingRaid.Members.Count == 2, ref failures);
			if (num == 2)
			{
				Check("original timeout still ends transferred raid without resetting elapsed time", raidManager.TryFailAndDisband(raid, out var failed) && failed.LeaderUserId == 62 && failed.PhaseClearTimeSeconds == 12, ref failures);
				continue;
			}
			Check($"state {num} current leader leaving disbands the raid", raidManager.Leave(62).Disbanded, ref failures);
		}
		bool[] array3 = new bool[2] { false, true };
		foreach (bool flag in array3)
		{
			using RaidWireClient raidWireClient = new RaidWireClient(61);
			using RaidWireClient raidWireClient2 = new RaidWireClient(62);
			using RaidWireClient raidWireClient3 = new RaidWireClient(63);
			using RaidWireClient raidWireClient4 = new RaidWireClient(64, 10201);
			using RaidWireClient raidWireClient5 = new RaidWireClient(65);
			SessionDirectory sessionDirectory = new SessionDirectory();
			RaidWireClient[] array4 = new RaidWireClient[5] { raidWireClient, raidWireClient2, raidWireClient3, raidWireClient4, raidWireClient5 };
			foreach (RaidWireClient raidWireClient6 in array4)
			{
				sessionDirectory.Register(raidWireClient6.Session.Player.CharacterId, raidWireClient6.Session);
				raidWireClient6.Session.Player.Name = new byte[1] { 65 };
			}
			raidWireClient5.Session.TcpClient.Dispose();
			RaidManager raidManager2 = new RaidManager();
			RaidSnapshot raid5 = raidManager2.Create(new byte[1] { 65 }, raidWireClient.Member, 200);
			raidManager2.TryAddMember(raid5.RaidId, raidWireClient2.Member, out raid5);
			if (flag)
			{
				raidManager2.TryBeginStart(61, out var raid6);
				raidManager2.TryCompletePreparation(raid6, out raid5);
			}
			RaidHandler raidHandler = new RaidHandler(DispatchProxy.Create<ICharacterRepository, UnusedDependency>(), sessionDirectory, raidManager2);
			raidHandler.HandleRaidManagerWork(raidWireClient.Session, new GamePacketHeader
			{
				type = 670
			}, RaidManagerCommand(1u, 62u)).GetAwaiter().GetResult();
			List<byte[]> list = raidWireClient.ReadThroughDirectory();
			Check($"active={flag} native transfer receives success ack", list[0][0] == 1 && BitConverter.ToUInt16(list[0], 1) == 670 && list[0][15] == 1, ref failures);
			array4 = new RaidWireClient[2] { raidWireClient2, raidWireClient3 };
			foreach (RaidWireClient raidWireClient7 in array4)
			{
				List<byte[]> list2 = raidWireClient7.ReadThroughDirectory();
				Check($"active={flag} client {raidWireClient7.Session.Player.UserId} receives new leader context info and directory", list2.Select((byte[] p) => BitConverter.ToUInt16(p, 1)).SequenceEqual(new ushort[3] { 2, 592, 591 }) && BitConverter.ToUInt16(list2[0], 56) == 62 && BitConverter.ToUInt32(list2[1], 19) == 2 && ReadRaidObjectLeader(list2[1], 23) == 62 && ReadRaidObjectLeader(list2[2], 19) == 62, ref failures);
				Check($"active={flag} client {raidWireClient7.Session.Player.UserId} retains same raid id state and count", BitConverter.ToUInt32(list2[1], 15) == raid5.RaidId && list2[1][33] == raid5.State && list2[2].Last() == 2, ref failures);
			}
			Check($"active={flag} other channel receives no leadership broadcast", raidWireClient4.Reader.Available == 0, ref failures);
			raidHandler.HandleRaidManagerWork(raidWireClient.Session, new GamePacketHeader
			{
				type = 670
			}, RaidManagerCommand(1u, 62u)).GetAwaiter().GetResult();
			Check($"active={flag} repeated request from old leader is rejected without broadcast", ReadManagerAck(raidWireClient)[15] == 0 && raidWireClient3.Reader.Available == 0 && raidWireClient2.Reader.Available == 0, ref failures);
			raidHandler.HandleRaidManagerWork(raidWireClient2.Session, new GamePacketHeader
			{
				type = 670
			}, RaidManagerCommand(1u, 63u)).GetAwaiter().GetResult();
			Check($"active={flag} online nonmember cannot be appointed leader", ReadManagerAck(raidWireClient2)[15] == 0 && raidManager2.TryGetByRaidId(raid5.RaidId, out var raid7) && raid7.LeaderUserId == 62, ref failures);
		}
	}

	private static void CheckRaidPartyLeave(ref int failures)
	{
		CheckRaidPartyLeaveGuards(ref failures);
		int[] array = new int[4] { 0, 1, 2, 3 };
		foreach (int num in array)
		{
			bool[] array2 = new bool[2] { false, true };
			foreach (bool flag in array2)
			{
				string text = $"raid party leave phase={num} leader={flag}";
				try
				{
					using RaidWireClient raidWireClient = new RaidWireClient(81);
					using RaidWireClient raidWireClient2 = new RaidWireClient(82);
					using RaidWireClient raidWireClient3 = new RaidWireClient(83);
					using RaidWireClient raidWireClient4 = new RaidWireClient(84, 10201);
					SessionDirectory sessionDirectory = new SessionDirectory();
					RaidWireClient[] array3 = new RaidWireClient[4] { raidWireClient, raidWireClient2, raidWireClient3, raidWireClient4 };
					foreach (RaidWireClient raidWireClient5 in array3)
					{
						RaidJoinFixture.Ready(raidWireClient5);
						sessionDirectory.Register(raidWireClient5.Session.Player.CharacterId, raidWireClient5.Session);
					}
					RaidManager raidManager = new RaidManager();
					PartyManager partyManager = new PartyManager();
					RaidSnapshot raid = raidManager.Create(new byte[1] { 65 }, raidWireClient.Member, 200);
					raidManager.TryAddMember(raid.RaidId, raidWireClient2.Member, out raid);
					Party party = partyManager.CreateParty(AsParty(raidWireClient)).Party;
					partyManager.Join(party.PartyId, AsParty(raidWireClient2));
					partyManager.UpdateSettings(81, raidWireClient.Session.SessionId, Array.Empty<byte>(), 4, new byte[12]
					{
						0, 0, 4, 255, 255, 255, 255, 5, 0, 2,
						0, 0
					});
					if (num > 0)
					{
						raidManager.TryBeginStart(81, out var raid2);
						raidManager.TryCompletePreparation(raid2, out raid);
					}
					if (num > 1)
					{
						raidManager.TryEnterPhaseBreak(raid.RaidId, out var _);
						raidManager.TryCompletePhase(raid.RaidId, out raid);
					}
					if (num > 2)
					{
						raidManager.TryPrepareNextPhase(81, out var raid4);
						raidManager.TryCompletePreparedNextPhase(raid4, partyManager.ArePreparedRaidPartiesReady, out raid);
					}
					ICharacterRepository characterRepository = DispatchProxy.Create<ICharacterRepository, RaidJoinCharacters>();
					RaidHandler raidHandler = new RaidHandler(characterRepository, sessionDirectory, raidManager);
					using var database = new RaidTestDatabase();
					using PartyHandler partyHandler = new PartyHandler(partyManager, characterRepository, sessionDirectory, null, null, null, null, database.Database);
					partyHandler.AttachRaidHandler(raidHandler);
					RaidWireClient raidWireClient6 = (flag ? raidWireClient : raidWireClient2);
					RaidWireClient raidWireClient7 = (flag ? raidWireClient2 : raidWireClient);
					ushort departedId = raidWireClient6.Session.Player.UserId;
					ushort remainingId = raidWireClient7.Session.Player.UserId;
					partyHandler.Handle_LEAVE_PARTY(raidWireClient6.Session, new GamePacketHeader
					{
						type = 13
					}, Array.Empty<byte>()).GetAwaiter().GetResult();
					raidManager.TryGetByRaidId(raid.RaidId, out var raid5);
					Check(text + " exits actual party and stays in same raid", partyManager.GetPartyByUser(departedId) == null && raid5.InstanceId == raid.InstanceId && raid5.Members.Count == 2 && raid5.LeaderUserId == 81, ref failures);
					Check(text + " departing roster is solo and remaining squad keeps its number", raid5.Members.Single((RaidMember m) => m.UserId == departedId).PartyIndex == 0 && raid5.Members.Single((RaidMember m) => m.UserId == remainingId).PartyIndex == 1 && partyManager.GetPartyByUser(remainingId).Count == 1, ref failures);
					Check(text + " preserves phase and separates situation groups", raid5.State == raid.State && raid5.PhaseIndex == raid.PhaseIndex && RaidManager.BuildSituationGroups(raid5.Members).Any((RaidSituationGroup g) => g.IsSolo && g.MemberKeys.SequenceEqual(new uint[1] { departedId })), ref failures);
					array3 = new RaidWireClient[3] { raidWireClient6, raidWireClient7, raidWireClient3 };
					foreach (RaidWireClient raidWireClient8 in array3)
					{
						List<byte[]> source = ReadAvailableRaidPackets(raidWireClient8);
						byte[][] array4 = source.Where((byte[] p) => BitConverter.ToUInt16(p, 1) == 592 && BitConverter.ToUInt32(p, 19) == 3).ToArray();
						Check(text + $" client {raidWireClient8.Session.Player.UserId} only receives solo departure and unchanged remaining squad", array4.Length != 0 && array4.All((byte[] p) => (from e in ReadSquadAssignments(p)
							orderby e.Key
							select e).SequenceEqual(new Dictionary<ushort, byte>
						{
							[departedId] = 0,
							[remainingId] = 1
						}.OrderBy((KeyValuePair<ushort, byte> e) => e.Key))), ref failures);
						if (raidWireClient8 != raidWireClient6)
						{
							continue;
						}
						Check(text + " departing client gets clear without re-formation", source.Any((byte[] p) => BitConverter.ToUInt16(p, 1) == 9 && p.Length >= 20 && p[19] == 3) && !source.Any((byte[] p) => BitConverter.ToUInt16(p, 1) == 9 && p.Length >= 80 && p[19] == 0 && Enumerable.Range(0, 8).Any((int slot) => BitConverter.ToUInt16(p, 36 + slot * 5) == departedId)), ref failures);
					}
					Check(text + " other channel receives no raid update", !ReadAvailableRaidPackets(raidWireClient4).Any((byte[] p) => BitConverter.ToUInt16(p, 1) == 592), ref failures);
					partyHandler.Handle_LEAVE_PARTY(raidWireClient6.Session, new GamePacketHeader
					{
						type = 13
					}, Array.Empty<byte>()).GetAwaiter().GetResult();
					Check(text + " repeated leave does not reassign or refresh others", raidWireClient3.Reader.Available == 0 && raidManager.TryGetByUser(departedId, out var raid6) && raid6.Members.Single((RaidMember m) => m.UserId == departedId).PartyIndex == 0, ref failures);
					partyHandler.Handle_LEAVE_PARTY(raidWireClient7.Session, new GamePacketHeader
					{
						type = 13
					}, Array.Empty<byte>()).GetAwaiter().GetResult();
					byte[][] array5 = (from p in ReadAvailableRaidPackets(raidWireClient3)
						where BitConverter.ToUInt16(p, 1) == 592 && BitConverter.ToUInt32(p, 19) == 3
						select p).ToArray();
					Check(text + " last party member leaves both users solo in raid and directory", partyManager.GetPartyByUser(remainingId) == null && partyManager.GetPartyByUser(departedId) == null && raidManager.TryGetByRaidId(raid.RaidId, out var raid7) && raid7.Members.Count == 2 && raid7.Members.All((RaidMember m) => m.PartyIndex == 0) && array5.Length != 0 && array5.All((byte[] p) => ReadSquadAssignments(p).Count == 2 && ReadSquadAssignments(p).Values.All((byte index) => index == 0)), ref failures);
				}
				catch (Exception ex)
				{
					Console.WriteLine(text + ": " + ex);
					Check(text + " completes", condition: false, ref failures);
				}
			}
		}
		static PartyMember AsParty(RaidWireClient client)
		{
			return new PartyMember
			{
				UserId = client.Session.Player.UserId,
				CharacterId = client.Session.Player.CharacterId,
				SessionId = client.Session.SessionId
			};
		}
	}

	private static void CheckRaidPartyLeaveGuards(ref int failures)
	{
		RaidManager raidManager = new RaidManager();
		PartyManager parties = new PartyManager();
		RaidMember member = new RaidMember
		{
			UserId = 85,
			CharacterId = 85u,
			SessionId = Guid.NewGuid(),
			PartyIndex = 1
		};
		RaidSnapshot raidSnapshot = raidManager.Create(Array.Empty<byte>(), member, 200);
		parties.CreateParty(new PartyMember
		{
			UserId = member.UserId,
			CharacterId = (int)member.CharacterId,
			SessionId = member.SessionId
		});
		int callbacks = 0;
		PartyOpResult partyOpResult = raidManager.LeaveNormalParty(member.UserId, Guid.NewGuid(), delegate
		{
			callbacks++;
			return parties.Leave(member.UserId, member.SessionId);
		}, out var updatedRaid);
		Check("raid party leave rejects stale session before ordinary membership mutation", !partyOpResult.Ok && callbacks == 0 && updatedRaid == null && parties.GetPartyByUser(member.UserId) != null && raidManager.TryGetByUser(member.UserId, out var raid) && raid.AssignmentVersion == raidSnapshot.AssignmentVersion && raid.Leader.PartyIndex == 1, ref failures);
		PartyOpResult partyOpResult2 = raidManager.LeaveNormalParty(member.UserId, member.SessionId, () => PartyOpResult.Fail("leave_rejected"), out updatedRaid);
		Check("failed ordinary leave preserves raid assignment", !partyOpResult2.Ok && updatedRaid == null && raidManager.TryGetByUser(member.UserId, out raid) && raid.AssignmentVersion == raidSnapshot.AssignmentVersion && raid.Leader.PartyIndex == 1, ref failures);
		raidManager.TryBeginStart(member.UserId, out var raid2);
		PartyOpResult partyOpResult3 = raidManager.LeaveNormalParty(member.UserId, member.SessionId, () => parties.Leave(member.UserId, member.SessionId), out updatedRaid);
		Check("successful solo departure invalidates frozen raid preparation", partyOpResult3.Ok && updatedRaid != null && updatedRaid.Leader.PartyIndex == 0 && updatedRaid.AssignmentVersion != raidSnapshot.AssignmentVersion && parties.GetPartyByUser(member.UserId) == null && !raidManager.TryCompletePreparation(raid2, out var _), ref failures);
	}

	private static List<byte[]> ReadAvailableRaidPackets(RaidWireClient client)
	{
		List<byte[]> list = new List<byte[]>();
		while (client.Reader.Available > 0)
		{
			list.Add(ReadManagerAck(client));
		}
		return list;
	}

	private static Dictionary<ushort, byte> ReadSquadAssignments(byte[] packet)
	{
		Dictionary<ushort, byte> dictionary = new Dictionary<ushort, byte>();
		int num = 24;
		for (int i = 0; i < packet[23]; i++)
		{
			int num2 = checked((int)BitConverter.ToUInt32(packet, num + 3));
			dictionary.Add(BitConverter.ToUInt16(packet, num), packet[num + 9 + num2]);
			num += 17 + num2;
		}
		return dictionary;
	}

	private static void CheckPartyRewardSelectionReplay(ref int failures)
	{
		Type flowType = typeof(RaidHandler).GetNestedType("PhaseRewardFlow", BindingFlags.NonPublic);
		MethodInfo build = typeof(RaidHandler).GetMethod("BuildPhaseOnePartyRewardEntries", BindingFlags.Static | BindingFlags.NonPublic);
		uint[] array = new uint[2] { 0u, 1u };
		foreach (uint num in array)
		{
			int[] array2 = new int[3] { 1, 2, 4 };
			foreach (int num2 in array2)
			{
				string scenario = $"phase {num} party reward replay size {num2}";
				RaidMember[] members = (from num4 in Enumerable.Range(0, num2)
					select new RaidMember
					{
						UserId = checked((ushort)(513 + num4 * 17)),
						PartyIndex = 2
					}).ToArray();
				ushort[] ids = members.Select((RaidMember m) => m.UserId).ToArray();
				object flow = Activator.CreateInstance(flowType, ids);
				uint materialConfig = AntonRaidRewardProvider.RollRewardContainer(num, "party_card", 1u);
				uint config = AntonRaidRewardProvider.RollRewardContainer(num, "gold", 1u);
				Check(scenario + " sealed cards send no fake owner or container", Rows(0, config).Length == 0 && Rows(1, materialConfig).Length == 0, ref failures);
				Check(scenario + " empty wire list contains no opening notification", RaidPacketBuilder.BuildRaidRewardList(1u, Rows(1, materialConfig)).SequenceEqual(new byte[2] { 1, 0 }), ref failures);
				for (int num3 = 0; num3 < num2; num3++)
				{
					byte b = checked((byte)(3 - num3));
					object[] array3 = Record(num3, 1, b);
					RaidRewardEntry[] array4 = Rows(1, materialConfig);
					Check(scenario + $" user {ids[num3]} opens only the chosen slot", (bool)array3[4] && array4.Length == num3 + 1 && array4[num3].UserId == ids[num3] && array4[num3].CardType == b, ref failures);
					Check(scenario + " item selection does not fabricate a gold selection", Rows(0, config).Length == 0, ref failures);
				}
				RaidRewardEntry[] array5 = Rows(1, materialConfig);
				Check(scenario + " material cache contains actual rewards only", array5.All((RaidRewardEntry r) => r.ItemId != 0 && r.ItemId != materialConfig && r.Quantity != 0), ref failures);
				byte[] array6 = RaidPacketBuilder.BuildRaidRewardList(1u, array5);
				Check(scenario + " late or repeated movie callback preserves exact reward bytes", array6.SequenceEqual(RaidPacketBuilder.BuildRaidRewardList(1u, Rows(1, materialConfig))), ref failures);
				Check(scenario + " duplicate selection is not another grant", !(bool)Record(0, 1, 3)[4], ref failures);
				Record(0, 0, 3);
				string name = scenario + " gold replay includes only its recorded user and slot";
				RaidRewardEntry[] array7 = Rows(0, config);
				Check(name, array7 != null && array7.Length == 1 && array7[0].UserId == ids[0] && array7[0].CardType == 3, ref failures);
				string name2 = scenario + " gold replay uses native currency id and bounded display count";
				RaidRewardEntry[] array8 = Rows(0, config);
				Check(name2, array8 != null && array8.Length == 1 && array8[0].ItemId == 0 && array8[0].Quantity == ((num == 0) ? 50000 : 65535), ref failures);
				Check(scenario + " gold notification does not change actual item replay", array6.SequenceEqual(RaidPacketBuilder.BuildRaidRewardList(1u, Rows(1, materialConfig))), ref failures);
				object[] Record(int index, byte type, byte card)
				{
					object[] array9 = new object[6]
					{
						ids[index],
						type,
						card,
						ids,
						false,
						false
					};
					if (!(bool)flowType.GetMethod("TryRecordCardOperation").Invoke(flow, array9))
					{
						throw new InvalidOperationException(scenario + " could not select a card");
					}
					return array9;
				}
				RaidRewardEntry[] Rows(byte type, uint num4)
				{
					return (RaidRewardEntry[])build.Invoke(null, new object[4] { flow, members, type, num4 });
				}
			}
		}
	}

	private static void CheckPartyRewardWireAndPersistence(ref int failures)
	{
		uint[] array = new uint[2] { 0u, 1u };
		foreach (uint num in array)
		{
			string text = $"phase {num} four-player reward wire";
			string text2 = Path.Combine(Path.GetTempPath(), "a21-raid-reward-wire-" + Guid.NewGuid().ToString("N") + ".db");
			List<RaidWireClient> list = new List<RaidWireClient>();
			try
			{
				GameDatabase db = new GameDatabase(text2, ServerPaths.SchemaFilePath);
				SessionDirectory sessionDirectory = new SessionDirectory();
				for (int j = 0; j < 4; j++)
				{
					checked
					{
						RaidWireClient raidWireClient = new RaidWireClient((ushort)(51001 + j * 17));
						list.Add(raidWireClient);
						int characterId = raidWireClient.Session.Player.CharacterId;
						using SqliteConnection sqliteConnection = db.OpenConnection();
						using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
						sqliteCommand.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@id,@name,''); INSERT INTO characters(character_id,account_id,name,level) VALUES(@id,@id,@bytes,85);";
						sqliteCommand.Parameters.AddWithValue("@id", characterId);
						sqliteCommand.Parameters.AddWithValue("@name", "reward-wire-" + characterId);
						sqliteCommand.Parameters.AddWithValue("@bytes", new byte[1] { (byte)(65 + j) });
						sqliteCommand.ExecuteNonQuery();
						InventoryContext.Register(raidWireClient.Session.SessionId, Load(characterId));
						sessionDirectory.Register(characterId, raidWireClient.Session);
					}
				}
				RaidManager raidManager = new RaidManager();
				RaidSnapshot raid = raidManager.Create(new byte[1] { 65 }, list[0].Member, 200);
				foreach (RaidWireClient item in list.Skip(1))
				{
					if (!raidManager.TryAddMember(raid.RaidId, item.Member, out raid))
					{
						throw new InvalidOperationException("Cannot add reward member");
					}
				}
				if (!raidManager.TryBeginStart(list[0].Member.UserId, out var raid2) || !raidManager.TryCompletePreparation(raid2, out raid))
				{
					throw new InvalidOperationException("Cannot start reward fixture");
				}
				if (num == 1 && (!raidManager.TryEnterPhaseBreak(raid.RaidId, out raid) || !raidManager.TryCompletePhase(raid.RaidId, out raid) || !raidManager.TryPrepareNextPhase(list[0].Member.UserId, out raid2) || !raidManager.TryCompletePreparedNextPhase(raid2, (IReadOnlyList<RaidMember> _) => true, out raid)))
				{
					throw new InvalidOperationException("Cannot start second-phase reward fixture");
				}
				if (!raidManager.TryEnterPhaseBreak(raid.RaidId, out raid))
				{
					throw new InvalidOperationException("Cannot enter result state");
				}
				RaidHandler handler = new RaidHandler(DispatchProxy.Create<ICharacterRepository, UnusedDependency>(), sessionDirectory, raidManager);
				Type nestedType = typeof(RaidHandler).GetNestedType("PhaseRewardFlow", BindingFlags.NonPublic);
				object obj = Activator.CreateInstance(nestedType, list.Select((RaidWireClient c) => c.Member.UserId).ToArray());
				string[] array2 = new string[4] { "TryStartResult", "TryStartCardSelection", "TryStartAutomaticCardSelection", "TryStartPartyRewardCompletion" };
				foreach (string name in array2)
				{
					nestedType.GetMethod(name).Invoke(obj, null);
				}
				object value = typeof(RaidHandler).GetField("_phaseRewardFlows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
				value.GetType().GetMethod("TryAdd").Invoke(value, new object[2] { raid.InstanceId, obj });
				foreach (RaidWireClient item2 in list.Take(3))
				{
					MovieFinished(item2);
					byte[][] array3 = Drain(item2).Where(IsReward).ToArray();
					Check(text + " initial screen receives two empty sealed lists", array3.Length == 2 && array3.All((byte[] p) => p.Length == 17 && p[16] == 0), ref failures);
				}
				Dictionary<ushort, (uint Item, int Count)> actualItems = new Dictionary<ushort, (uint, int)>();
				for (int num3 = 0; num3 < 4; num3++)
				{
					RaidWireClient selecting = list[num3];
					byte card = checked((byte)(3 - num3));
					Select(selecting, 1, card);
					byte[][] itemRows = list.SelectMany(Drain).Where(IsReward).ToArray();
					Check(text + $" selection {num3} broadcasts actual item to all four members", itemRows.Length == 4 && itemRows.All((byte[] p) => p.Length == 27 && p[15] == 1 && p[16] == 1 && BitConverter.ToUInt16(p, 17) == selecting.Member.UserId && p[19] == card && BitConverter.ToUInt32(p, 21) != 0 && BitConverter.ToUInt16(p, 25) > 0) && itemRows.All((byte[] p) => p.SequenceEqual(itemRows[0])), ref failures);
					uint num4 = BitConverter.ToUInt32(itemRows[0], 21);
					int num5 = BitConverter.ToUInt16(itemRows[0], 25);
					actualItems[selecting.Member.UserId] = (num4, num5);
					Check(text + $" selection {num3} actual item persisted exactly as displayed", Load(selecting.Member.UserId).CountMainItem((int)num4) == num5, ref failures);
					Select(selecting, 0, card);
					List<byte[]> source = null;
					List<byte[]> list2 = new List<byte[]>();
					foreach (RaidWireClient item3 in list)
					{
						List<byte[]> list3 = Drain(item3);
						list2.AddRange(list3.Where(IsReward));
						if (item3 == selecting)
						{
							source = list3;
							continue;
						}
						Check(text + " gold receipt is private to its owner", list3.All((byte[] p) => BitConverter.ToUInt16(p, 1) != 503), ref failures);
					}
					int amount = ((num == 0) ? 50000 : 120000);
					Check(text + $" selection {num3} gold native id and u16 display", list2.Count == 4 && list2.All((byte[] p) => p.Length == 27 && p[15] == 0 && BitConverter.ToUInt32(p, 21) == 0 && BitConverter.ToUInt16(p, 25) == Math.Min(amount, 65535)), ref failures);
					Check(text + $" selection {num3} full gold amount survives database reload", Load(selecting.Member.UserId).GetMainVirtualCount(0).Count == amount, ref failures);
					byte[][] array4 = source.Where((byte[] p) => BitConverter.ToUInt16(p, 1) == 503).ToArray();
					byte[] array5 = GamePacketEnvelopeBuilder.Build(0, 503, ServerNoticeMessageBuilder.BuildRaidNotice("团本金币奖励：牌面 65,535 金币，额外 54,465 金币，合计 120,000 金币已到账。", 0));
					Check(text + $" selection {num3} receipt explains only overflow gold", (num == 0) ? (array4.Length == 0) : (array4.Length == 1 && array4[0].SequenceEqual(array5)), ref failures);
					Select(selecting, 1, card);
					Select(selecting, 0, card);
					byte[][] array6 = list.SelectMany(Drain).ToArray();
					Check(text + $" selection {num3} repeated clicks only acknowledge without duplicate rewards", array6.Length == 2 && array6.All((byte[] p) => BitConverter.ToUInt16(p, 1) == 661) && Load(selecting.Member.UserId).GetMainVirtualCount(0).Count == amount && Load(selecting.Member.UserId).CountMainItem((int)num4) == num5, ref failures);
					if (num3 == 0)
					{
						MovieFinished(list[3]);
						byte[][] array7 = Drain(list[3]).Where(IsReward).ToArray();
						Check(text + " late movie finish replays only the selected card and actual item", array7.Length == 2 && array7.All((byte[] p) => p.Length == 27 && p[16] == 1 && BitConverter.ToUInt16(p, 17) == selecting.Member.UserId && p[19] == card) && BitConverter.ToUInt32(array7.Single((byte[] p) => p[15] == 1), 21) == num4, ref failures);
					}
				}
				MovieFinished(list[3]);
				byte[][] replay = Drain(list[3]).Where(IsReward).ToArray();
				Check(text + " repeated movie finish preserves four actual item rows", replay.Length == 2 && replay.All((byte[] p) => p.Length == 57 && p[16] == 4) && Enumerable.Range(0, 4).All(delegate(int num7)
				{
					byte[] value2 = replay.Single((byte[] p) => p[15] == 1);
					int num6 = 17 + num7 * 10;
					(uint, int) value3;
					return actualItems.TryGetValue(BitConverter.ToUInt16(value2, num6), out value3) && BitConverter.ToUInt32(value2, num6 + 4) == value3.Item1 && BitConverter.ToUInt16(value2, num6 + 8) == value3.Item2;
				}), ref failures);
				if (num != 1)
				{
					continue;
				}
				((Task)typeof(RaidHandler).GetMethod("ShowPhaseOneSquadRewardsAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(handler, new object[2] { raid, "selftest" })).GetAwaiter().GetResult();
				byte[][] array8 = (from p in list.SelectMany(Drain)
					where IsReward(p) && p[15] == 3
					select p).ToArray();
				Check("second-phase actual handler broadcasts four gold big cards to all four clients", array8.Length == 4 && array8.All((byte[] p) => p.Length == 57 && p[16] == 4 && Enumerable.Range(0, 4).All((int num6) => p[20 + 10 * num6] == 1 && AntonRaidRewardProvider.GetSquadDisplayFlags(BitConverter.ToUInt32(p, 21 + 10 * num6), ItemMetadataResolver.Resolve((int)BitConverter.ToUInt32(p, 21 + 10 * num6))) == 1)), ref failures);
				InventoryService Load(int id)
				{
					using SqliteConnection connection = db.OpenConnection();
					return InventoryService.LoadFromDb(connection, id, id, db);
				}
				void MovieFinished(RaidWireClient client)
				{
					handler.HandleRaidMovieSkip(client.Session, new GamePacketHeader
					{
						type = 660
					}, new byte[1] { 1 }).GetAwaiter().GetResult();
				}
				void Select(RaidWireClient client, byte type, byte b)
				{
					handler.HandleSelectRaidRewardCard(client.Session, new GamePacketHeader
					{
						type = 661
					}, new byte[2] { type, b }).GetAwaiter().GetResult();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(text + ": " + ex);
				Check(text + " integration completed", condition: false, ref failures);
			}
			finally
			{
				foreach (RaidWireClient item4 in list)
				{
					InventoryContext.Unregister(item4.Session.SessionId);
					item4.Dispose();
				}
				SqliteConnection.ClearAllPools();
				string[] array2 = new string[3] { "", "-wal", "-shm" };
				foreach (string text3 in array2)
				{
					if (File.Exists(text2 + text3))
					{
						File.Delete(text2 + text3);
					}
				}
			}
		}
		static List<byte[]> Drain(RaidWireClient client)
		{
			client.Session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 65000, Array.Empty<byte>())).GetAwaiter().GetResult();
			List<byte[]> list4 = new List<byte[]>();
			while (true)
			{
				byte[] array9 = ReadManagerAck(client);
				if (BitConverter.ToUInt16(array9, 1) == 65000)
				{
					break;
				}
				list4.Add(array9);
			}
			return list4;
		}
		static bool IsReward(byte[] packet)
		{
			return BitConverter.ToUInt16(packet, 1) == 601;
		}
	}

	private static void CheckRaidTownJoinRefresh(ref int failures)
	{
		using RaidWireClient raidWireClient = new RaidWireClient(51);
		using RaidWireClient raidWireClient2 = new RaidWireClient(52);
		using RaidWireClient raidWireClient3 = new RaidWireClient(53, 10201);
		using RaidWireClient raidWireClient4 = new RaidWireClient(54, 10011);
		SessionDirectory sessionDirectory = new SessionDirectory();
		RaidWireClient[] array = new RaidWireClient[4] { raidWireClient, raidWireClient2, raidWireClient3, raidWireClient4 };
		foreach (RaidWireClient raidWireClient5 in array)
		{
			sessionDirectory.Register(raidWireClient5.Session.Player.CharacterId, raidWireClient5.Session);
			raidWireClient5.Session.Player.CurTownId = 19;
			raidWireClient5.Session.Player.CurAreaId = 1;
			raidWireClient5.Session.Player.Name = new byte[1] { 65 };
			raidWireClient5.Session.Player.UserState = 0;
		}
		RaidManager raidManager = new RaidManager();
		RaidSnapshot raidSnapshot = raidManager.Create(new byte[1] { 65 }, raidWireClient.Member, 200);
		ICharacterRepository characterRepository = DispatchProxy.Create<ICharacterRepository, UnusedDependency>();
		RaidHandler raidHandler = new RaidHandler(characterRepository, sessionDirectory, raidManager);
		Check("raid town fixture uses actual PVF Seria room", Town.IsCeraRoom(19, 0) && !Town.IsCeraRoom(19, 1), ref failures);
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient2.Session, 19, 0).GetAwaiter().GetResult();
		List<byte[]> list = raidWireClient2.ReadThroughDirectory();
		Check("leaving Seria restores raid leader context before object and list", list.Select((byte[] p) => BitConverter.ToUInt16(p, 1)).SequenceEqual(new ushort[3] { 2, 592, 591 }) && BitConverter.ToUInt16(list[0], 56) == 51, ref failures);
		int condition;
		if (BitConverter.ToUInt32(list[list.Count - 1], 15) == 1)
		{
			if (BitConverter.ToUInt32(list[list.Count - 1], 19) == raidSnapshot.RaidId)
			{
				condition = ((list[list.Count - 1].Last() == 1) ? 1 : 0);
				goto IL_0223;
			}
		}
		condition = 0;
		goto IL_0223;
		IL_0223:
		Check("arrival list includes the recruiting raid and correct member count", (byte)condition != 0, ref failures);
		Check("arrival refresh does not join the applicant", !raidManager.TryGetByUser(52, out var raid), ref failures);
		Check("arrival refresh only targets the arriving client", raidWireClient.Reader.Available == 0 && raidWireClient3.Reader.Available == 0, ref failures);
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient2.Session, 19, 1).GetAwaiter().GetResult();
		Check("repeated same-area packet does not replay directory", raidWireClient2.Reader.Available == 0, ref failures);
		raidWireClient2.Session.Player.CurAreaId = 0;
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient2.Session, 19, 1).GetAwaiter().GetResult();
		Check("entering private Seria room does not replay directory", raidWireClient2.Reader.Available == 0, ref failures);
		raidWireClient2.Session.Player.CurAreaId = 1;
		raidWireClient2.Session.Player.TownPresenceReady = false;
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient2.Session, 19, 0).GetAwaiter().GetResult();
		Check("incomplete town load does not replay directory", raidWireClient2.Reader.Available == 0, ref failures);
		raidWireClient2.Session.Player.TownPresenceReady = true;
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient4.Session, 19, 0).GetAwaiter().GetResult();
		Check("ordinary channel never receives raid arrival packets", raidWireClient4.Reader.Available == 0, ref failures);
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient.Session, 19, 0).GetAwaiter().GetResult();
		Check("existing raid member does not replay raid state on area change", raidWireClient.Reader.Available == 0, ref failures);
		raidHandler.HandleRaidTownAreaChangedAsync(raidWireClient3.Session, 19, 0).GetAwaiter().GetResult();
		Check("non-raid listener receives no arrival directory", raidWireClient3.Reader.Available == 0, ref failures);
		TownHandler townHandler = new TownHandler(characterRepository, null, null, sessionDirectory);
		townHandler.ConfigureRaidTownAreaChanged(raidHandler.HandleRaidTownAreaChangedAsync);
		TaskCompletionSource globalLists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource publisherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		townHandler.ConfigureTownPartyListPublisher(delegate
		{
			publisherEntered.TrySetResult();
			return globalLists.Task;
		});
		raidWireClient2.Session.Player.CurAreaId = 0;
		raidWireClient.Session.Player.CurAreaId = 2;
		Task task = townHandler.Handle_ENUM_CMDPACKET_SET_USER_AREA(raidWireClient2.Session, new GamePacketHeader
		{
			type = 36
		}, new byte[6] { 19, 1, 100, 0, 100, 0 });
		try
		{
			publisherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5L)).GetAwaiter().GetResult();
			Check("slow global party lists do not finish the area handler yet", !task.IsCompleted, ref failures);
			List<byte[]> list2 = raidWireClient2.ReadThroughDirectory();
			ushort[] array2 = list2.Select((byte[] p) => BitConverter.ToUInt16(p, 1)).ToArray();
			Check("actual Seria exit sends finish-load before waiting on slow global lists", ((ReadOnlySpan<ushort>)array2).Contains((ushort)1213), ref failures);
			Check("actual Seria exit orders local roster then readiness then raid directory", Array.IndexOf(array2, (ushort)24) < Array.IndexOf(array2, (ushort)1213) && Array.IndexOf(array2, (ushort)1213) < Array.IndexOf(array2, (ushort)592) && array2.Last() == 591, ref failures);
			Check("actual Seria exit refresh preserves recruiting raid and does not join", BitConverter.ToUInt32(list2[list2.Count - 1], 19) == raidSnapshot.RaidId && !raidManager.TryGetByUser(52, out raid), ref failures);
		}
		finally
		{
			globalLists.TrySetResult();
			task.WaitAsync(TimeSpan.FromSeconds(5L)).GetAwaiter().GetResult();
		}
		Check("area completion sends no duplicate readiness notification", raidWireClient2.Reader.Available == 0, ref failures);
	}
}
