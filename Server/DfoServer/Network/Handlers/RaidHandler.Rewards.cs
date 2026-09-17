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
			if (ResultStarted)
			{
				return Interlocked.CompareExchange(ref _cardSelectionStarted, 1, 0) == 0;
			}
			return false;
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
				if (_selectedCardIndexes.TryGetValue(userId, out var value))
				{
					return value;
				}
				HashSet<ushort> partyIds = new HashSet<ushort>(partyUserIds ?? Array.Empty<ushort>());
				HashSet<byte> hashSet = new HashSet<byte>(_selectedCardIndexes.Where(delegate(KeyValuePair<ushort, byte> entry)
				{
					HashSet<ushort> hashSet2 = partyIds;
					KeyValuePair<ushort, byte> keyValuePair = entry;
					return hashSet2.Contains(keyValuePair.Key);
				}).Select(delegate(KeyValuePair<ushort, byte> entry)
				{
					KeyValuePair<ushort, byte> keyValuePair = entry;
					return keyValuePair.Value;
				}));
				if (requestedCardIndex < 4 && !hashSet.Contains(requestedCardIndex))
				{
					_selectedCardIndexes[userId] = requestedCardIndex;
					return requestedCardIndex;
				}
				for (byte b = 0; b < 4; b++)
				{
					if (!hashSet.Contains(b))
					{
						_selectedCardIndexes[userId] = b;
						return b;
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

		public bool TryGetSelectedPartyCard(ushort userId, byte rewardType, out byte cardIndex)
		{
			lock (_sync)
			{
				cardIndex = 0;
				byte value;
				return rewardType <= 1 && _rewardOperations.TryGetValue(userId, out value) && (value & (1 << (int)rewardType)) != 0 && _selectedCardIndexes.TryGetValue(userId, out cardIndex);
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
				if (_selectedCardIndexes.TryGetValue(userId, out var value) && value != cardIndex)
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
				_rewardOperations.TryGetValue(userId, out var value2);
				byte b = (byte)(1 << (int)rewardType);
				recordedNow = (value2 & b) == 0;
				_rewardOperations[userId] = (byte)(value2 | b);
				allSelected = _eligibleUserIds.Count == 0 || _eligibleUserIds.All((ushort eligibleUserId) => _rewardOperations.TryGetValue(eligibleUserId, out var value3) && (value3 & 3) == 3);
				return true;
			}
		}

		public bool TryStartAutomaticCardSelection()
		{
			if (CardSelectionStarted)
			{
				return Interlocked.CompareExchange(ref _automaticCardSelectionStarted, 1, 0) == 0;
			}
			return false;
		}

		public bool TryStartPartyRewardCompletion()
		{
			if (CardSelectionStarted)
			{
				return Interlocked.CompareExchange(ref _partyRewardCompletionStarted, 1, 0) == 0;
			}
			return false;
		}

		public bool TryStartSquadReward()
		{
			if (CardSelectionStarted)
			{
				return Interlocked.CompareExchange(ref _squadRewardStarted, 1, 0) == 0;
			}
			return false;
		}

		public bool TryFinish()
		{
			if (Volatile.Read(in _squadRewardStarted) != 0)
			{
				return Interlocked.CompareExchange(ref _finished, 1, 0) == 0;
			}
			return false;
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
			RaidMember raidMember = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
			if (raidMember != null)
			{
				await SendPhaseOnePartyRewardListsAsync(session, raid, flow, raidMember);
			}
		}
		FileLogger.Log($"[GameProtocol] RAID_MOVIE_SKIP raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())} finished={movieFinished}");
	}

	internal static bool IsRaidMovieFinishedRequest(byte[] body)
	{
		if (body != null && body.Length != 0)
		{
			return body[0] == 1;
		}
		return false;
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
		if (_phaseRewardFlows.TryGetValue(raidId, out var value) && !value.CardSelectionStarted && _raids.TryGetByRaidId(raidId, out var raid) && raid.State == 3)
		{
			await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_MOVIE_SKIP, RaidPacketBuilder.BuildRaidMovieSkip(0u, 0u));
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_MOVIE_PROMPT raid={raidId}");
		}
	}

	private async Task<bool> SendPhaseOneResultAsync(PhaseRewardFlow flow, RaidMember member, uint phaseIndex, uint clearTimeSeconds, uint deathCount)
	{
		int characterId = checked((int)member.CharacterId);
		if (!_sessions.TryGet(characterId, out var session) || session.SessionId != member.SessionId)
		{
			return false;
		}
		byte rewardOption = ((!flow.IsEligible(member.UserId)) ? ((byte)1) : ((byte)0));
		uint clientRank = GetAntonClientPhaseRank(phaseIndex, deathCount);
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_RESULT, RaidPacketBuilder.BuildRaidResult(0u, phaseIndex, clearTimeSeconds, deathCount, clientRank, rewardOption)));
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_RESULT user={member.UserId} rank={clientRank} deaths={deathCount} clearTime={clearTimeSeconds}");
		return true;
	}

	private async Task SendPhaseOnePartyRewardListsAsync(EnhancedClientSession session, RaidSnapshot raid, PhaseRewardFlow flow, RaidMember receiver)
	{
		RaidMember[] partyMembers = raid.Members.Where((RaidMember member) => member.PartyIndex == receiver.PartyIndex && flow.IsEligible(member.UserId)).ToArray();
		if (partyMembers.Length != 0)
		{
			RaidRewardEntry[] rewards = BuildPhaseOnePartyRewardEntries(flow, partyMembers, 0, AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "gold", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
			RaidRewardEntry[] partyCardRewards = BuildPhaseOnePartyRewardEntries(flow, partyMembers, 1, AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "party_card", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_REWARD_LIST, RaidPacketBuilder.BuildRaidRewardList(0u, rewards)));
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_REWARD_LIST, RaidPacketBuilder.BuildRaidRewardList(1u, partyCardRewards)));
			if (flow.TryStartAutomaticCardSelection())
			{
				RunInBackground(AutoSelectPendingPhaseOneCardsAsync(raid.RaidId), "phase-one-auto-card");
			}
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_PARTY_REWARD_LIST raid={raid.RaidId} phase={raid.PhaseIndex} user={receiver.UserId} party={receiver.PartyIndex} members={partyMembers.Length} revealedGold={rewards.Length} revealedItems={partyCardRewards.Length}");
		}
	}

	private static RaidRewardEntry[] BuildPhaseOnePartyRewardEntries(PhaseRewardFlow flow, IReadOnlyList<RaidMember> partyMembers, byte rewardType, uint configurationItemId)
	{
		List<RaidRewardEntry> list = new List<RaidRewardEntry>();
		foreach (RaidMember partyMember in partyMembers)
		{
			if (flow.TryGetSelectedPartyCard(partyMember.UserId, rewardType, out var cardIndex))
			{
				if (!flow.TryGetOrCreatePartyCardReward(partyMember.PartyIndex, rewardType, cardIndex, configurationItemId, out var reward))
				{
					FileLogger.Log($"[GameProtocol] RAID_REWARD config resolve failed party={partyMember.PartyIndex} card={cardIndex} type={rewardType} config={configurationItemId}");
				}
				else
				{
					list.Add(BuildPhaseOnePartyCardRevealEntry(partyMember.UserId, cardIndex, GetPhaseOnePartyCardDisplayItemId(rewardType, configurationItemId, reward.ItemId), GetPhaseOnePartyCardDisplayCount(rewardType, reward.Count)));
				}
			}
		}
		return list.ToArray();
	}

	private async Task AutoSelectPendingPhaseOneCardsAsync(uint raidId)
	{
		await Task.Delay(10000);
		if (!_phaseRewardFlows.TryGetValue(raidId, out var flow) || !_raids.TryGetByRaidId(raidId, out var raid) || raid.State != 3)
		{
			return;
		}
		ushort[] pendingUserIds = flow.GetPendingUserIds();
		ushort[] array = pendingUserIds;
		foreach (ushort pendingUserId in array)
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
			byte[] array2 = new byte[2] { 1, 0 };
			byte[] array3 = array2;
			foreach (byte rewardType in array3)
			{
				if (flow.TryRecordCardOperation(pendingUserId, rewardType, cardIndex, partyUserIds, out var recordedNow, out var _) & recordedNow)
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
		if (!_phaseRewardFlows.TryGetValue(raidId, out var value) || !value.TryStartCardSelection())
		{
			return Task.CompletedTask;
		}
		FileLogger.Log($"[GameProtocol] RAID_PHASE1_CARD_REWARD raid={raidId} reason={reason} eligible={value.EligibleCount}");
		return Task.CompletedTask;
	}

	public async Task HandleSelectRaidRewardCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		bool flag = TryReadRaidRewardCardRequest(body, out var rewardType, out var cardIndex);
		ushort userId = 0;
		RaidSnapshot raid = null;
		PhaseRewardFlow flow = null;
		bool ok = flag && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 3 && _phaseRewardFlows.TryGetValue(raid.RaidId, out flow) && flow.CardSelectionStarted && flow.IsEligible(userId);
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
		if ((ok & recordedNow) && rewardType <= 1)
		{
			await SendPhaseOnePartyCardRevealAsync(raid, flow, userId, rewardType);
		}
		await SendAckAsync(session, header.type, ok);
		if (ok & recordedNow)
		{
			RaidMember raidMember = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
			if (raidMember != null)
			{
				await GrantPhaseOnePartyRewardAsync(raid, raidMember, rewardType, cardIndex);
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
		if (_phaseRewardFlows.TryGetValue(raidId, out var value) && value.TryStartPartyRewardCompletion())
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
		if (selectedCardIndex > 3)
		{
			return -1;
		}
		return selectedCardIndex;
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
		if (rewardType <= 2)
		{
			return cardIndex <= 3;
		}
		return false;
	}

	private async Task SendPhaseOnePartyCardRevealAsync(RaidSnapshot raid, PhaseRewardFlow flow, ushort userId, byte rewardType)
	{
		RaidMember member = raid.Members.FirstOrDefault((RaidMember entry) => entry.UserId == userId);
		uint configurationItemId = ((rewardType == 1) ? AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "party_card", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)) : AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "gold", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
		if (member != null && flow.TryGetSelectedCardIndex(userId, out var selectedCardIndex) && flow.TryGetOrCreatePartyCardReward(member.PartyIndex, rewardType, selectedCardIndex, configurationItemId, out var reward))
		{
			uint displayItemId = GetPhaseOnePartyCardDisplayItemId(rewardType, configurationItemId, reward.ItemId);
			int displayCount = GetPhaseOnePartyCardDisplayCount(rewardType, reward.Count);
			RaidRewardEntry[] rewards = new RaidRewardEntry[1] { BuildPhaseOnePartyCardRevealEntry(member.UserId, selectedCardIndex, displayItemId, displayCount) };
			byte[] packet = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_REWARD_LIST, RaidPacketBuilder.BuildRaidRewardList(rewardType, rewards));
			await _sessions.BroadcastToAsync(from entry in raid.Members
				where entry.PartyIndex == member.PartyIndex
				select checked((int)entry.CharacterId), packet);
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_PARTY_REWARD_REVEAL raid={raid.RaidId} user={member.UserId} card={selectedCardIndex} template={configurationItemId} display={displayItemId} actual={reward.ItemId} count={reward.Count} displayCount={displayCount}");
		}
	}

	internal static uint GetPhaseOnePartyCardDisplayItemId(byte rewardType, uint configurationItemId, uint rewardItemId)
	{
		return rewardItemId;
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
		if (_phaseRewardFlows.TryGetValue(raid.RaidId, out var value))
		{
			int partyRewardIndex = GetPartyRewardIndex(cardIndex);
			uint configurationItemId = ((rewardType == 1) ? AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "party_card", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)) : AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "gold", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount)));
			if (partyRewardIndex < 0 || !value.TryGetOrCreatePartyCardReward(member.PartyIndex, rewardType, cardIndex, configurationItemId, out var reward))
			{
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_CARD_GRANTED resolve failed raid={raid.RaidId} user={member.UserId} type={rewardType} card={cardIndex}");
			}
			else
			{
				bool value2 = await GrantResolvedRaidRewardAsync(member, reward);
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_CARD_GRANTED raid={raid.RaidId} user={member.UserId} type={rewardType} card={cardIndex} item={reward.ItemId} count={reward.Count} granted={value2}");
			}
		}
	}

	private async Task ShowPhaseOneSquadRewardsAsync(uint raidId, string reason)
	{
		if (!_phaseRewardFlows.TryGetValue(raidId, out var flow) || !flow.TryStartSquadReward() || !_raids.TryGetByRaidId(raidId, out var raid) || raid.State != 3)
		{
			return;
		}
		List<(RaidMember Member, uint ConfigurationItemId, ResolvedRaidReward Reward, byte Flags)> resolvedRewards = new List<(RaidMember, uint, ResolvedRaidReward, byte)>();
		checked
		{
			foreach (RaidMember item in raid.Members.Where((RaidMember raidMember) => flow.IsEligible(raidMember.UserId)))
			{
				uint num = AntonRaidRewardProvider.RollRewardContainer(raid.PhaseIndex, "squad_item", GetAntonPhaseRank(raid.PhaseIndex, raid.PhaseDeathCount), out var flags);
				if (!TryRollConfiguredRaidReward(num, out var itemId, out var count))
				{
					FileLogger.Log($"[GameProtocol] RAID_REWARD config resolve failed user={item.UserId} type={3u} config={num}");
					continue;
				}
				byte squadDisplayFlags = AntonRaidRewardProvider.GetSquadDisplayFlags(itemId, (itemId == 0) ? null : ItemMetadataResolver.Resolve((int)itemId));
				FileLogger.Log($"[GameProtocol] RAID_GOLD_CLASSIFICATION raid={raidId} phase={raid.PhaseIndex} user={item.UserId} config={num} item={itemId} configuredFlag={flags} actualFlag={squadDisplayFlags}");
				resolvedRewards.Add((item, num, new ResolvedRaidReward(itemId, count), squadDisplayFlags));
			}
			RaidRewardEntry[] rewards = resolvedRewards.Select(((RaidMember Member, uint ConfigurationItemId, ResolvedRaidReward Reward, byte Flags) tuple) => new RaidRewardEntry
			{
				UserId = tuple.Member.UserId,
				CardType = 1,
				Quantity = (uint)tuple.Reward.Count,
				ItemId = tuple.Reward.ItemId,
				Flags = tuple.Flags
			}).ToArray();
			await BroadcastRaidNotificationAsync(raid, NotiPacketTypeA21.RAID_REWARD_LIST, RaidPacketBuilder.BuildRaidRewardList(3u, rewards));
			await Task.Delay(5000);
			foreach (var entry in resolvedRewards)
			{
				bool value = await GrantResolvedRaidRewardAsync(entry.Member, entry.Reward);
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_SQUAD_REWARD_GRANTED user={entry.Member.UserId} config={entry.ConfigurationItemId} item={entry.Reward.ItemId} count={entry.Reward.Count} granted={value}");
			}
			RunInBackground(FinishPhaseOneRewardsAfterDelayAsync(raidId), "phase-one-finish-delay");
			FileLogger.Log($"[GameProtocol] RAID_PHASE1_SQUAD_REWARD raid={raidId} reason={reason} rewards={rewards.Length}");
		}
	}

	private async Task FinishPhaseOneRewardsAfterDelayAsync(uint raidId)
	{
		if (_raids.TryGetByRaidId(raidId, out var raid) && raid.State == 3 && _phaseRewardFlows.TryGetValue(raidId, out var value) && value.TryFinish() && _raids.TryCompletePhase(raidId, out var completed))
		{
			PhaseRewardFlow value2;
			if (completed.PhaseIndex == 1)
			{
				CancelAllPhaseTwoTimers(raidId);
				await EnablePhaseOneDungeonReturnAsync(completed);
				_phaseRewardFlows.TryRemove(raidId, out value2);
				CleanupRaidRuntimeState(raidId);
				FileLogger.Log($"[GameProtocol] RAID_PHASE2_COMPLETE raid={raidId} state={completed.State}");
			}
			else
			{
				await BroadcastRaidStateAsync(completed);
				uint remainingBreakSeconds = GetAntonPhaseBreakRemainingSeconds();
				await BroadcastRaidNotificationAsync(completed, NotiPacketTypeA21.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(0u, 0u, remainingBreakSeconds));
				await BroadcastRaidNotificationAsync(completed, NotiPacketTypeA21.RAID_REMAIN_TIME, RaidPacketBuilder.BuildRemainTime(1, remainingBreakSeconds));
				await EnablePhaseOneDungeonReturnAsync(completed);
				_phaseRewardFlows.TryRemove(raidId, out value2);
				SchedulePhaseBreakTimer(completed, remainingBreakSeconds);
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_BREAK raid={raidId} state={completed.State} break={remainingBreakSeconds}");
			}
		}
	}

	internal static uint GetAntonPhaseBreakRemainingSeconds()
	{
		uint phaseBreakSeconds = AntonRaidRewardProvider.GetPhaseBreakSeconds();
		if (phaseBreakSeconds <= 5)
		{
			return 0u;
		}
		return phaseBreakSeconds - 5;
	}

	private async Task EnablePhaseOneDungeonReturnAsync(RaidSnapshot raid)
	{
		foreach (RaidMember member in raid.Members)
		{
			int characterId = checked((int)member.CharacterId);
			if (_sessions.TryGet(characterId, out var session) && _raids.TryGetCompletedTownReturn(member.UserId, member.SessionId, out var raid2) && raid2.InstanceId == raid.InstanceId && TownHandler.IsCompletedRaidTownReturn(session.ListenerPort, session.Player, session.SessionId, raid2))
			{
				await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.ENABLE_CLEAR_DUNGEON, DungeonNotificationBuilder.BuildEnableClearDungeon()));
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_ENABLE_RETURN_TOWN raid={raid.RaidId} user={member.UserId}");
				continue;
			}
			if (_sessions.TryGet(characterId, out var session2) && !(session2.SessionId != member.SessionId) && session2.Player?.CurrentRun != null && IsAntonRaidDungeon(session2.Player.CurrentRun.DungeonId))
			{
				await session2.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.ENABLE_CLEAR_DUNGEON, DungeonNotificationBuilder.BuildEnableClearDungeon()));
				FileLogger.Log($"[GameProtocol] RAID_PHASE1_ENABLE_RETURN raid={raid.RaidId} user={member.UserId} dungeon={session2.Player.CurrentRun.DungeonId}");
			}
			session2 = null;
		}
	}

	internal static uint SelectAntonPhaseOneSquadReward(int roll)
	{
		int num = 100;
		if (roll < 0 || roll >= num)
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
		BoosterRewardEntry[] array = StackableItemProvider.Load(checked((int)configurationItemId))?.UpgradableLegacyRewards?.Where((BoosterRewardEntry val) => val.ItemId >= 0 && val.Weight > 0).ToArray();
		if (array == null || array.Length == 0)
		{
			return false;
		}
		int num = array.Sum((BoosterRewardEntry val) => val.Weight);
		if (num <= 0)
		{
			return false;
		}
		int num2 = Random.Shared.Next(num);
		BoosterRewardEntry[] array2 = array;
		foreach (BoosterRewardEntry boosterRewardEntry in array2)
		{
			num2 -= boosterRewardEntry.Weight;
			if (num2 < 0)
			{
				itemId = checked((uint)boosterRewardEntry.ItemId);
				count = Math.Max(1, boosterRewardEntry.Count);
				return true;
			}
		}
		return false;
	}

	private Task<bool> GrantResolvedRaidRewardAsync(RaidMember member, ResolvedRaidReward reward)
	{
		if (reward.ItemId != 0)
		{
			return GrantRaidRewardAsync(member, reward.ItemId, reward.Count);
		}
		return GrantRaidGoldRewardAsync(member, reward.Count);
	}

	private async Task<bool> GrantRaidGoldRewardAsync(RaidMember member, int amount)
	{
		int num = checked((int)member.CharacterId);
		if (!_sessions.TryGet(num, out var session) || session.SessionId != member.SessionId || !InventoryContext.TryGetLease(num, out var lease) || !lease.IsOwnedBy(member.SessionId))
		{
			return false;
		}
		if (!RaidRewardCommitService.TryGrantGold(lease, amount, out var grantedCount))
		{
			FileLogger.Log($"[GameProtocol] RAID_REWARD gold commit failed cid={num} amount={amount}");
			return false;
		}
		await InventoryRefreshSender.SendOnlineUpdateItemList(session, InventoryListType.Main, new short[1]);
		if (grantedCount < amount)
		{
			string message = FormattableString.Invariant($"团本金币奖励：背包金币达到携带上限，本次实际到账 {grantedCount:N0} 金币。");
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice(message, 0)));
		}
		else if (amount > 65535)
		{
			string message2 = FormattableString.Invariant($"团本金币奖励：牌面 {ushort.MaxValue:N0} 金币，额外 {amount - 65535:N0} 金币，合计 {amount:N0} 金币已到账。");
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.BuildRaidNotice(message2, 0)));
		}
		return true;
	}

	private async Task<bool> GrantRaidRewardAsync(RaidMember member, uint itemId, int count)
	{
		checked
		{
			int num = (int)member.CharacterId;
			if (!_sessions.TryGet(num, out var session) || session.SessionId != member.SessionId || !InventoryContext.TryGetLease(num, out var lease) || !lease.IsOwnedBy(member.SessionId))
			{
				return false;
			}
			if (!RaidRewardCommitService.TryGrantItem(lease, (int)itemId, count, out var changes))
			{
				FileLogger.Log($"[GameProtocol] RAID_REWARD item commit failed cid={num} item={itemId} count={count}");
				return false;
			}
			foreach (IGrouping<InventoryListType, InventorySlotMutation> item in changes.GroupBy(delegate(InventorySlotMutation change)
			{
				InventorySlotMutation inventorySlotMutation = change;
				return inventorySlotMutation.ListType;
			}))
			{
				await InventoryRefreshSender.SendOnlineUpdateItemList(session, item.Key, item.Select((InventorySlotMutation change) => change.SlotIndex));
			}
			return true;
		}
	}

	internal static int GetPhaseOnePartyCardDisplayCount(byte rewardType, int rewardCount)
	{
		if (rewardCount <= 0)
		{
			throw new ArgumentOutOfRangeException("rewardCount");
		}
		if (rewardType != 0)
		{
			return rewardCount;
		}
		return Math.Min(rewardCount, 65535);
	}
}
