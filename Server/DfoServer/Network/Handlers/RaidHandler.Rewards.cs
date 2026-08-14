using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;
using PvfLib;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	private sealed class ResolvedRaidReward
	{
		public uint ItemId { get; }

		public int Count { get; }

		public ResolvedRaidReward(uint itemId, int count)
		{
			ItemId = itemId;
			Count = count;
		}
	}

	private sealed class PhaseRewardFlow
	{
		private readonly object _sync = new object();

		private readonly HashSet<ushort> _eligibleUserIds;

		private readonly Dictionary<ushort, byte> _selectedCardIndexes = new Dictionary<ushort, byte>();

		private readonly Dictionary<ushort, byte> _rewardOperations = new Dictionary<ushort, byte>();

		private readonly Dictionary<(ushort UserId, byte RewardType), ResolvedRaidReward> _resolvedRewards = new Dictionary<(ushort, byte), ResolvedRaidReward>();

		private readonly Dictionary<(ushort PartyIndex, byte RewardType, byte CardIndex), ResolvedRaidReward> _partyCardRewards = new Dictionary<(ushort, byte, byte), ResolvedRaidReward>();

		private int _resultStarted;

		private int _cardSelectionStarted;

		private int _partyRewardCompletionStarted;

		private int _automaticCardSelectionStarted;

		private int _squadRewardStarted;

		private int _finished;

		public int EligibleCount => _eligibleUserIds.Count;

		public bool ResultStarted => Volatile.Read(in _resultStarted) != 0;

		public bool CardSelectionStarted => Volatile.Read(in _cardSelectionStarted) != 0;

		public PhaseRewardFlow(IEnumerable<ushort> eligibleUserIds)
		{
			_eligibleUserIds = new HashSet<ushort>(eligibleUserIds ?? Array.Empty<ushort>());
		}

		public bool IsEligible(ushort userId)
		{
			return _eligibleUserIds.Contains(userId);
		}

		public bool TryStartResult()
		{
			return Interlocked.CompareExchange(ref _resultStarted, 1, 0) == 0;
		}

		public bool TryStartCardSelection()
		{
			return ResultStarted && Interlocked.CompareExchange(ref _cardSelectionStarted, 1, 0) == 0;
		}

		public ushort[] GetPendingUserIds()
		{
			lock (_sync)
			{
				return _eligibleUserIds.Where((ushort userId) => !_rewardOperations.TryGetValue(userId, out var value) || (value & 3) != 3).ToArray();
			}
		}

		public byte GetOrCreateAvailableCardIndex(ushort userId, byte requestedCardIndex, IEnumerable<ushort> partyUserIds)
		{
			lock (_sync)
			{
				if (_selectedCardIndexes.TryGetValue(userId, out var selectedCardIndex))
				{
					return selectedCardIndex;
				}
				HashSet<ushort> partyIds = new HashSet<ushort>(partyUserIds ?? Array.Empty<ushort>());
				HashSet<byte> occupiedIndexes = new HashSet<byte>(from entry in _selectedCardIndexes
					where partyIds.Contains(entry.Key)
					select entry.Value);
				if (requestedCardIndex < 4 && !occupiedIndexes.Contains(requestedCardIndex))
				{
					_selectedCardIndexes[userId] = requestedCardIndex;
					return requestedCardIndex;
				}
				for (byte candidate = 0; candidate < 4; candidate++)
				{
					if (!occupiedIndexes.Contains(candidate))
					{
						_selectedCardIndexes[userId] = candidate;
						return candidate;
					}
				}
				_selectedCardIndexes[userId] = 0;
				return 0;
			}
		}

		public bool TryGetSelectedCardIndex(ushort userId, out byte cardIndex)
		{
			lock (_sync)
			{
				return _selectedCardIndexes.TryGetValue(userId, out cardIndex);
			}
		}

		public bool TryGetOrCreateResolvedReward(ushort userId, byte rewardType, uint configurationItemId, out ResolvedRaidReward reward)
		{
			lock (_sync)
			{
				reward = null;
				if (!_eligibleUserIds.Contains(userId))
				{
					return false;
				}
				(ushort, byte) key = (userId, rewardType);
				if (_resolvedRewards.TryGetValue(key, out reward))
				{
					return true;
				}
				if (!TryRollConfiguredRaidReward(configurationItemId, out var itemId, out var count))
				{
					return false;
				}
				reward = new ResolvedRaidReward(itemId, count);
				_resolvedRewards[key] = reward;
				return true;
			}
		}

		public bool TryGetOrCreatePartyCardReward(ushort partyIndex, byte rewardType, byte cardIndex, uint configurationItemId, out ResolvedRaidReward reward)
		{
			lock (_sync)
			{
				reward = null;
				if (cardIndex > 3)
				{
					return false;
				}
				(ushort, byte, byte) key = (partyIndex, rewardType, cardIndex);
				if (_partyCardRewards.TryGetValue(key, out reward))
				{
					return true;
				}
				if (!TryRollConfiguredRaidReward(configurationItemId, out var itemId, out var count))
				{
					return false;
				}
				reward = new ResolvedRaidReward(itemId, count);
				_partyCardRewards[key] = reward;
				return true;
			}
		}

		public bool TryRecordCardOperation(ushort userId, byte rewardType, byte cardIndex, IEnumerable<ushort> partyUserIds, out bool recordedNow, out bool allSelected)
		{
			lock (_sync)
			{
				recordedNow = false;
				allSelected = false;
				if (!_eligibleUserIds.Contains(userId) || rewardType > 2 || cardIndex > 3)
				{
					return false;
				}
				if (_selectedCardIndexes.TryGetValue(userId, out var selectedCardIndex) && selectedCardIndex != cardIndex)
				{
					return false;
				}
				if (!_selectedCardIndexes.ContainsKey(userId))
				{
					HashSet<ushort> partyIds = new HashSet<ushort>(partyUserIds ?? Array.Empty<ushort>());
					if (_selectedCardIndexes.Any((KeyValuePair<ushort, byte> entry) => entry.Key != userId && partyIds.Contains(entry.Key) && entry.Value == cardIndex))
					{
						return false;
					}
				}
				_selectedCardIndexes[userId] = cardIndex;
				_rewardOperations.TryGetValue(userId, out var operations);
				byte operationBit = (byte)(1 << (int)rewardType);
				recordedNow = (operations & operationBit) == 0;
				_rewardOperations[userId] = (byte)(operations | operationBit);
				allSelected = _eligibleUserIds.Count == 0 || _eligibleUserIds.All((ushort eligibleUserId) => _rewardOperations.TryGetValue(eligibleUserId, out var value) && (value & 3) == 3);
				return true;
			}
		}

		public bool TryStartAutomaticCardSelection()
		{
			return CardSelectionStarted && Interlocked.CompareExchange(ref _automaticCardSelectionStarted, 1, 0) == 0;
		}

		public bool TryStartPartyRewardCompletion()
		{
			return CardSelectionStarted && Interlocked.CompareExchange(ref _partyRewardCompletionStarted, 1, 0) == 0;
		}

		public bool TryStartSquadReward()
		{
			return CardSelectionStarted && Interlocked.CompareExchange(ref _squadRewardStarted, 1, 0) == 0;
		}

		public bool TryFinish()
		{
			return Volatile.Read(in _squadRewardStarted) != 0 && Interlocked.CompareExchange(ref _finished, 1, 0) == 0;
		}
	}

	public async Task HandleRaidMovieSkip(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		PhaseRewardFlow flow = null;
		bool ok = TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 3 && _phaseRewardFlows.TryGetValue(raid.RaidId, out flow) && flow.ResultStarted;
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_MOVIE_SKIP body=" + BitConverter.ToString(body ?? Array.Empty<byte>()) + " ok=false");
			return;
		}
		bool movieFinished = IsRaidMovieFinishedRequest(body);
		if (movieFinished)
		{
			await BeginPhaseOneCardSelectionAsync(raid.RaidId, "movie-finished");
			await SendRaidStateValueAsync(session, 4u, raid.StateArgument);
			RaidMember member = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
			if (member != null)
			{
				await SendPhaseOnePartyRewardListsAsync(session, raid, flow, member);
			}
		}
		FileLogger.Log($"[GameProtocol] RAID_MOVIE_SKIP raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())} finished={movieFinished}");
	}

	internal static bool IsRaidMovieFinishedRequest(byte[] body)
	{
		return body != null && body.Length != 0 && body[0] == 1;
	}

	private async Task StartPhaseOneResultMovieAsync(uint raidId)
	{
		if (!_phaseRewardFlows.TryGetValue(raidId, out var flow) || !flow.TryStartResult() || !_raids.TryGetByRaidId(raidId, out var raid) || raid.State != 3)
		{
			return;
		}
		int resultSent = 0;
		using (IEnumerator<RaidMember> enumerator = raid.Members.GetEnumerator())
		{
			while (enumerator.MoveNext())
			{
				if (await SendPhaseOneResultAsync(member: enumerator.Current, flow: flow, phaseIndex: raid.PhaseIndex, clearTimeSeconds: raid.PhaseClearTimeSeconds, deathCount: raid.PhaseDeathCount))
				{
					resultSent++;
				}
			}
		}
		await BroadcastRaidStateAsync(raid);
		RunInBackground(ShowPhaseOneMovieSkipPromptAsync(raidId), "phase-one-movie-prompt");
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_MOVIE raid={raidId} eligible={flow.EligibleCount} sent={resultSent}");
	}

	private async Task ShowPhaseOneMovieSkipPromptAsync(uint raidId)
	{
		await Task.Delay(2000);
		if (_phaseRewardFlows.TryGetValue(raidId, out var flow) && !flow.CardSelectionStarted && _raids.TryGetByRaidId(raidId, out var raid) && raid.State == 3)
		{
			await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_MOVIE_SKIP, RaidPacketBuilder.BuildRaidMovieSkip(0u, 0u));
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_MOVIE_PROMPT raid={raidId}");
		}
	}

	private async Task<bool> SendPhaseOneResultAsync(PhaseRewardFlow flow, RaidMember member, uint phaseIndex, uint clearTimeSeconds, uint deathCount)
	{
		int characterId = checked((int)member.CharacterId);
		if (!_sessions.TryGet(characterId, out var memberSession) || memberSession.SessionId != member.SessionId)
		{
			return false;
		}
		byte rewardOption = ((!flow.IsEligible(member.UserId)) ? ((byte)1) : ((byte)0));
		uint clientRank = GetAntonClientPhaseRank(phaseIndex, deathCount);
		await memberSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 602, RaidPacketBuilder.BuildRaidResult(0u, phaseIndex, clearTimeSeconds, deathCount, clientRank, rewardOption)));
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_RESULT user={member.UserId} rank={clientRank} deaths={deathCount} clearTime={clearTimeSeconds}");
		return true;
	}

	private async Task SendPhaseOnePartyRewardListsAsync(EnhancedClientSession session, RaidSnapshot raid, PhaseRewardFlow flow, RaidMember receiver)
	{
		RaidMember[] partyMembers = raid.Members.Where((RaidMember member) => member.PartyIndex == receiver.PartyIndex && flow.IsEligible(member.UserId)).ToArray();
		if (partyMembers.Length != 0)
		{
			RaidRewardEntry[] goldRewards = BuildPhaseOnePartyRewardEntries(flow, partyMembers, 0, AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "gold", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
			RaidRewardEntry[] partyCardRewards = BuildPhaseOnePartyRewardEntries(flow, partyMembers, 1, AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "party_card", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 601, RaidPacketBuilder.BuildRaidRewardList(0u, goldRewards)));
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 601, RaidPacketBuilder.BuildRaidRewardList(1u, partyCardRewards)));
			if (flow.TryStartAutomaticCardSelection())
			{
				RunInBackground(AutoSelectPendingPhaseOneCardsAsync(raid.RaidId), "phase-one-auto-card");
			}
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_PARTY_REWARD_LIST raid={raid.RaidId} user={receiver.UserId} party={receiver.PartyIndex} cards={partyMembers.Length}");
		}
	}

	private static RaidRewardEntry[] BuildPhaseOnePartyRewardEntries(PhaseRewardFlow flow, IReadOnlyList<RaidMember> partyMembers, byte rewardType, uint configurationItemId)
	{
		List<RaidRewardEntry> entries = new List<RaidRewardEntry>();
		ushort partyIndex = partyMembers[0].PartyIndex;
		for (int cardIndex = 0; cardIndex < 4; cardIndex++)
		{
			if (!flow.TryGetOrCreatePartyCardReward(partyIndex, rewardType, checked((byte)cardIndex), configurationItemId, out var reward))
			{
				FileLogger.Log($"[GameProtocol] RAID_REWARD config resolve failed party={partyIndex} card={cardIndex} type={rewardType} config={configurationItemId}");
			}
			else
			{
				RaidMember initialOwner = partyMembers[cardIndex % partyMembers.Count];
				entries.Add(checked(new RaidRewardEntry
				{
					UserId = initialOwner.UserId,
					CardType = (byte)cardIndex,
					Quantity = ((rewardType != 0) ? 1u : ((uint)reward.Count)),
					ItemId = configurationItemId,
					Flags = 0u
				}));
			}
		}
		return entries.ToArray();
	}

	private async Task AutoSelectPendingPhaseOneCardsAsync(uint raidId)
	{
		await Task.Delay(10000);
		if (!_phaseRewardFlows.TryGetValue(raidId, out var flow) || !_raids.TryGetByRaidId(raidId, out var raid) || raid.State != 3)
		{
			return;
		}
		ushort[] pendingUserIds = flow.GetPendingUserIds();
		foreach (ushort pendingUserId in pendingUserIds)
		{
			RaidMember member = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == pendingUserId);
			if (member == null)
			{
				continue;
			}
			ushort[] partyUserIds = (from entry in raid.Members
				where entry.PartyIndex == member.PartyIndex && flow.IsEligible(entry.UserId)
				select entry.UserId).ToArray();
			byte cardIndex = flow.GetOrCreateAvailableCardIndex(pendingUserId, 0, partyUserIds);
			byte[] array = new byte[2] { 1, 0 };
			foreach (byte rewardType in array)
			{
				if (flow.TryRecordCardOperation(pendingUserId, rewardType, cardIndex, partyUserIds, out var recordedNow, out var _) && recordedNow)
				{
					await SendPhaseOnePartyCardRevealAsync(raid, flow, pendingUserId, rewardType);
					await GrantPhaseOnePartyRewardAsync(raid, member, rewardType, cardIndex);
				}
			}
		}
		RunInBackground(CompletePhaseOnePartyRewardsAfterRevealAsync(raidId), "phase-one-party-reward-complete");
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_AUTO_CARD_SELECTION raid={raidId}");
	}

	private Task BeginPhaseOneCardSelectionAsync(uint raidId, string reason)
	{
		if (!_phaseRewardFlows.TryGetValue(raidId, out var flow) || !flow.TryStartCardSelection())
		{
			return Task.CompletedTask;
		}
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_CARD_REWARD raid={raidId} reason={reason} eligible={flow.EligibleCount}");
		return Task.CompletedTask;
	}

	public async Task HandleSelectRaidRewardCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		byte rewardType;
		byte cardIndex;
		bool requestValid = TryReadRaidRewardCardRequest(body, out rewardType, out cardIndex);
		ushort userId = 0;
		RaidSnapshot raid = null;
		PhaseRewardFlow flow = null;
		bool ok = requestValid && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 3 && _phaseRewardFlows.TryGetValue(raid.RaidId, out flow) && flow.CardSelectionStarted && flow.IsEligible(userId);
		bool recordedNow = false;
		bool allSelected = false;
		if (ok)
		{
			RaidMember member = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
			ushort[] partyUserIds = ((member == null) ? Array.Empty<ushort>() : (from entry in raid.Members
				where entry.PartyIndex == member.PartyIndex && flow.IsEligible(entry.UserId)
				select entry.UserId).ToArray());
			cardIndex = flow.GetOrCreateAvailableCardIndex(userId, cardIndex, partyUserIds);
			ok = flow.TryRecordCardOperation(userId, rewardType, cardIndex, partyUserIds, out recordedNow, out allSelected);
		}
		if (ok && recordedNow && rewardType <= 1)
		{
			await SendPhaseOnePartyCardRevealAsync(raid, flow, userId, rewardType);
		}
		await SendAckAsync(session, header.type, ok);
		if (ok && recordedNow)
		{
			RaidMember selectedMember = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
			if (selectedMember != null)
			{
				await GrantPhaseOnePartyRewardAsync(raid, selectedMember, rewardType, cardIndex);
			}
		}
		if (allSelected)
		{
			RunInBackground(CompletePhaseOnePartyRewardsAfterRevealAsync(raid.RaidId), "phase-one-selected-reward-complete");
		}
		FileLogger.Log($"[GameProtocol] SELECT_RAID_REWARD_CARD body={BitConverter.ToString(body ?? Array.Empty<byte>())} type={rewardType} card={cardIndex} ok={ok} recorded={recordedNow} all={allSelected}");
	}

	private async Task CompletePhaseOnePartyRewardsAfterRevealAsync(uint raidId)
	{
		if (_phaseRewardFlows.TryGetValue(raidId, out var flow) && flow.TryStartPartyRewardCompletion())
		{
			await Task.Delay(2000);
			if (_raids.TryGetByRaidId(raidId, out var raid) && raid.State == 3)
			{
				await ShowPhaseOneSquadRewardsAsync(raidId, "client-selection-complete");
			}
		}
	}

	internal static ushort GetPartyRewardOrdinal(int entryIndex)
	{
		if (entryIndex < 0 || entryIndex > 3)
		{
			throw new ArgumentOutOfRangeException("entryIndex");
		}
		return checked((ushort)(entryIndex + 1));
	}

	internal static int GetPartyRewardIndex(byte selectedCardIndex)
	{
		return (selectedCardIndex <= 3) ? selectedCardIndex : (-1);
	}

	internal static bool TryReadRaidRewardCardRequest(byte[] body, out byte rewardType, out byte cardIndex)
	{
		rewardType = byte.MaxValue;
		cardIndex = byte.MaxValue;
		if (body == null || body.Length != 2)
		{
			return false;
		}
		rewardType = body[0];
		cardIndex = body[1];
		return rewardType <= 2 && cardIndex <= 3;
	}

	private async Task SendPhaseOnePartyCardRevealAsync(RaidSnapshot raid, PhaseRewardFlow flow, ushort userId, byte rewardType)
	{
		RaidMember member = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
		uint configurationItemId = ((rewardType == 1) ? AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "party_card", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)) : AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "gold", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
		if (member != null && flow.TryGetSelectedCardIndex(userId, out var selectedCardIndex) && flow.TryGetOrCreatePartyCardReward(member.PartyIndex, rewardType, selectedCardIndex, configurationItemId, out var reward))
		{
			uint displayItemId = GetPhaseOnePartyCardDisplayItemId(rewardType, configurationItemId, reward.ItemId);
			int displayCount = reward.Count;
			RaidRewardEntry[] entries = new RaidRewardEntry[1] { BuildPhaseOnePartyCardRevealEntry(member.UserId, selectedCardIndex, displayItemId, displayCount) };
			byte[] packet = GamePacketEnvelopeBuilder.Build(0, 601, RaidPacketBuilder.BuildRaidRewardList(rewardType, entries));
			await _sessions.BroadcastToAsync(from entry in raid.Members
				where entry.PartyIndex == member.PartyIndex
				select checked((int)entry.CharacterId), packet);
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_PARTY_REWARD_REVEAL raid={raid.RaidId} user={member.UserId} card={selectedCardIndex} template={configurationItemId} display={displayItemId} actual={reward.ItemId} count={reward.Count} displayCount={displayCount}");
		}
	}

	internal static uint GetPhaseOnePartyCardDisplayItemId(byte rewardType, uint configurationItemId, uint rewardItemId)
	{
		return (rewardType == 0) ? configurationItemId : rewardItemId;
	}

	internal static RaidRewardEntry BuildPhaseOnePartyCardRevealEntry(ushort userId, byte cardIndex, uint itemId, int count)
	{
		if (cardIndex > 3)
		{
			throw new ArgumentOutOfRangeException("cardIndex");
		}
		if (count <= 0)
		{
			throw new ArgumentOutOfRangeException("count");
		}
		return new RaidRewardEntry
		{
			UserId = userId,
			CardType = cardIndex,
			Quantity = checked((uint)count),
			ItemId = itemId,
			Flags = 0u
		};
	}

	internal static uint GetAntonPhaseRank(uint deathCount)
	{
		return GetAntonPhaseRank(0u, deathCount);
	}

	internal static uint GetAntonPhaseRank(uint phaseIndex, uint deathCount)
	{
		return AntonRaidRewardProvider.GetPhaseRank(phaseIndex, deathCount);
	}

	internal static uint GetAntonClientPhaseRank(uint deathCount)
	{
		return GetAntonClientPhaseRank(0u, deathCount);
	}

	internal static uint GetAntonClientPhaseRank(uint phaseIndex, uint deathCount)
	{
		return checked(GetAntonPhaseRank(phaseIndex, deathCount) + 2);
	}

	private async Task GrantPhaseOnePartyRewardAsync(RaidSnapshot raid, RaidMember member, byte rewardType, byte cardIndex)
	{
		if (_phaseRewardFlows.TryGetValue(raid.RaidId, out var flow))
		{
			int rewardIndex = GetPartyRewardIndex(cardIndex);
			uint configurationItemId = ((rewardType == 1) ? AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "party_card", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)) : AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "gold", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
			if (rewardIndex < 0 || !flow.TryGetOrCreatePartyCardReward(member.PartyIndex, rewardType, cardIndex, configurationItemId, out var reward))
			{
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_CARD_GRANTED resolve failed raid={raid.RaidId} user={member.UserId} type={rewardType} card={cardIndex}");
			}
			else
			{
				bool granted = await GrantResolvedRaidRewardAsync(member, reward);
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_CARD_GRANTED raid={raid.RaidId} user={member.UserId} type={rewardType} card={cardIndex} item={reward.ItemId} count={reward.Count} granted={granted}");
			}
		}
	}

	private async Task ShowPhaseOneSquadRewardsAsync(uint raidId, string reason)
	{
		if (!_phaseRewardFlows.TryGetValue(raidId, out var flow) || !flow.TryStartSquadReward() || !_raids.TryGetByRaidId(raidId, out var raid) || raid.State != 3)
		{
			return;
		}
		List<(RaidMember Member, uint ConfigurationItemId, ResolvedRaidReward Reward)> resolvedRewards = new List<(RaidMember, uint, ResolvedRaidReward)>();
		foreach (RaidMember member in raid.Members.Where((RaidMember raidMember) => flow.IsEligible(raidMember.UserId)))
		{
			uint configurationItemId = AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "squad_item", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount));
			if (!TryRollConfiguredRaidReward(configurationItemId, out var itemId, out var count))
			{
				FileLogger.Log($"[GameProtocol] RAID_REWARD config resolve failed user={member.UserId} type={3u} config={configurationItemId}");
			}
			else
			{
				resolvedRewards.Add((member, configurationItemId, new ResolvedRaidReward(itemId, count)));
			}
		}
		RaidRewardEntry[] rewards = resolvedRewards.Select(((RaidMember Member, uint ConfigurationItemId, ResolvedRaidReward Reward) tuple) => new RaidRewardEntry
		{
			UserId = tuple.Member.UserId,
			CardType = 1,
			Quantity = checked((uint)tuple.Reward.Count),
			ItemId = tuple.Reward.ItemId,
			Flags = 0u
		}).ToArray();
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_REWARD_LIST, RaidPacketBuilder.BuildRaidRewardList(3u, rewards));
		await Task.Delay(5000);
		foreach (var entry in resolvedRewards)
		{
			bool granted = await GrantResolvedRaidRewardAsync(entry.Member, entry.Reward);
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_SQUAD_REWARD_GRANTED user={entry.Member.UserId} config={entry.ConfigurationItemId} item={entry.Reward.ItemId} count={entry.Reward.Count} granted={granted}");
		}
		RunInBackground(FinishPhaseOneRewardsAfterDelayAsync(raidId), "phase-one-finish-delay");
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_SQUAD_REWARD raid={raidId} reason={reason} rewards={rewards.Length}");
	}

	private async Task FinishPhaseOneRewardsAfterDelayAsync(uint raidId)
	{
		if (_raids.TryGetByRaidId(raidId, out var rewardDisplayRaid) && rewardDisplayRaid.State == 3 && _phaseRewardFlows.TryGetValue(raidId, out var flow) && flow.TryFinish() && _raids.TryCompletePhase(raidId, out var completed))
		{
			PhaseRewardFlow value;
			if (completed.PhaseIndex == 1)
			{
				CancelAllPhaseTwoTimers(raidId);
				await EnablePhaseOneDungeonReturnAsync(completed);
				_phaseRewardFlows.TryRemove(raidId, out value);
				CleanupRaidRuntimeState(raidId);
				FileLogger.Log($"[GameProtocol] RAID_PHASE2_COMPLETE raid={raidId} state={completed.State}");
			}
			else
			{
				await BroadcastRaidStateAsync(completed);
				uint remainingBreakSeconds = GetAntonPhaseBreakRemainingSeconds();
				await BroadcastRaidNotificationAsync(completed, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, remainingBreakSeconds));
				await BroadcastRaidNotificationAsync(completed, NotiPacketType.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(1, remainingBreakSeconds));
				await EnablePhaseOneDungeonReturnAsync(completed);
				_phaseRewardFlows.TryRemove(raidId, out value);
				RunInBackground(RunPhaseBreakTimerAsync(raidId, remainingBreakSeconds), "phase-break");
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_BREAK raid={raidId} state={completed.State} break={remainingBreakSeconds}");
			}
		}
	}

	internal static uint GetAntonPhaseBreakRemainingSeconds()
	{
		uint configuredSeconds = AntonRaidRewardProvider.GetPhaseBreakSeconds();
		return (configuredSeconds > 5) ? (configuredSeconds - 5) : 0u;
	}

	private async Task EnablePhaseOneDungeonReturnAsync(RaidSnapshot raid)
	{
		foreach (RaidMember member in raid.Members)
		{
			int characterId = checked((int)member.CharacterId);
			if (_sessions.TryGet(characterId, out var session) && !(session.SessionId != member.SessionId) && session.Player?.CurrentRun != null && IsAntonRaidDungeon(session.Player.CurrentRun.DungeonId))
			{
				await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 31, DungeonNotificationBuilder.BuildEnableClearDungeon()));
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_ENABLE_RETURN raid={raid.RaidId} user={member.UserId} dungeon={session.Player.CurrentRun.DungeonId}");
				session = null;
			}
		}
	}

	internal static uint SelectAntonPhaseOneSquadReward(int roll)
	{
		int totalWeight = 100;
		if (roll < 0 || roll >= totalWeight)
		{
			throw new ArgumentOutOfRangeException("roll");
		}
		if (roll < 92)
		{
			return 10094731u;
		}
		if (roll < 95)
		{
			return 10094737u;
		}
		return 10094784u;
	}

	private static bool TryRollConfiguredRaidReward(uint configurationItemId, out uint itemId, out int count)
	{
		itemId = 0u;
		count = 0;
		StackableItemFile obj = StackableItemProvider.Load(checked((int)configurationItemId));
		BoosterRewardEntry[] rewards = ((obj == null) ? null : obj.UpgradableLegacyRewards?.Where((BoosterRewardEntry val) => val.ItemId >= 0 && val.Weight > 0).ToArray());
		if (rewards == null || rewards.Length == 0)
		{
			return false;
		}
		int totalWeight = rewards.Sum((BoosterRewardEntry val) => val.Weight);
		if (totalWeight <= 0)
		{
			return false;
		}
		int roll = Random.Shared.Next(totalWeight);
		BoosterRewardEntry[] array = rewards;
		foreach (BoosterRewardEntry reward in array)
		{
			roll -= reward.Weight;
			if (roll < 0)
			{
				itemId = checked((uint)reward.ItemId);
				count = Math.Max(1, reward.Count);
				return true;
			}
		}
		return false;
	}

	private Task<bool> GrantResolvedRaidRewardAsync(RaidMember member, ResolvedRaidReward reward)
	{
		return (reward.ItemId == 0) ? GrantRaidGoldRewardAsync(member, reward.Count) : GrantRaidRewardAsync(member, reward.ItemId, reward.Count);
	}

	private async Task<bool> GrantRaidGoldRewardAsync(RaidMember member, int amount)
	{
		int characterId = checked((int)member.CharacterId);
		if (!_sessions.TryGet(characterId, out var session) || session.SessionId != member.SessionId || !InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(member.SessionId))
		{
			return false;
		}
		var committed = RaidRewardCommitService.TryGrantGold(lease, amount);
		if (!committed)
		{
			FileLogger.Log($"[GameProtocol] RAID_REWARD gold commit failed cid={characterId} amount={amount}");
			return false;
		}
		await InventoryRefreshSender.SendOnlineUpdateItemList(session, InventoryListType.Main, new short[1]);
		return true;
	}

	private async Task<bool> GrantRaidRewardAsync(RaidMember member, uint itemId, int count)
	{
		checked
		{
			int characterId = (int)member.CharacterId;
			if (!_sessions.TryGet(characterId, out var session) || session.SessionId != member.SessionId || !InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(member.SessionId))
			{
				return false;
			}
			var committed = RaidRewardCommitService.TryGrantItem(
				lease,
				(int)itemId,
				count,
				out var changes);
			if (!committed)
			{
				FileLogger.Log($"[GameProtocol] RAID_REWARD item commit failed cid={characterId} item={itemId} count={count}");
				return false;
			}
			foreach (IGrouping<InventoryListType, InventorySlotMutation> group in from change in changes
				group change by change.ListType)
			{
				await InventoryRefreshSender.SendOnlineUpdateItemList(session, group.Key, group.Select((InventorySlotMutation change) => change.SlotIndex));
			}
			return true;
		}
	}
}
