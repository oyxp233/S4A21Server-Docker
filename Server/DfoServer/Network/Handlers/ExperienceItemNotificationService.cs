using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class ExperienceItemNotificationService
    {
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly SqliteSubtype1Repository _subtype1Repository;
        private readonly HonorLevelSyncService _honorLevel;

        internal ExperienceItemNotificationService(
            ICharacterRepository characterRepository,
            string databasePath,
            string schemaFilePath)
            : this(
                characterRepository,
                new GameDatabase(databasePath, schemaFilePath))
        {
        }

        internal ExperienceItemNotificationService(
            ICharacterRepository characterRepository,
            IGameDatabase database)
        {
            _characterRepository = characterRepository
                ?? throw new ArgumentNullException(nameof(characterRepository));
            if (database == null) throw new ArgumentNullException(nameof(database));

            _subtype0Repository = new SqliteSubtype0FieldsRepository(database);
            _subtype1Repository = new SqliteSubtype1Repository(database);
            _honorLevel = new HonorLevelSyncService(
                characterRepository,
                database);
        }

        internal async Task SendAsync(
            EnhancedClientSession session,
            ExperienceItemUseResult result,
            bool sendQuestList = true)
        {
            if (session?.Player == null || result == null || !result.Success)
                return;

            var honor = ResolveHonor(result, session.Player.Subtype0Tail);
            ApplyHonorToSession(session, honor);
            var growthCapsule = result.NewLevel >= ExpTableProvider.MaxLevel
                ? GrowthCapsuleDataProvider.Calculate(result.TotalGrowthCapsuleExp)
                : GrowthCapsuleDataProvider.Calculate(0);
            var leveledUp = result.NewLevel > result.PreviousLevel;
            var inDungeon = session.Player.CurrentRun != null;

            if (!leveledUp)
            {
                await SendExperienceAsync(session, result, honor, growthCapsule);
            }
            else if (inDungeon)
            {
                // 副本内发送 subtype0 不安全。既有升级路径先发送 EXP 快照，
                // 再刷新任务和派生的 subtype1 属性。
                await SendExperienceAsync(session, result, honor, growthCapsule);
                if (sendQuestList)
                {
                    await TrySendAuxiliaryAsync(
                        "quest-list",
                        () => SendQuestListAsync(session));
                }
                await TrySendAuxiliaryAsync(
                    "subtype1",
                    () => SendSubtype1Async(session, result, honor));
            }
            else
            {
                await TrySendAuxiliaryAsync(
                    "subtype0",
                    () => SendSubtype0Async(session, honor));
                await TrySendAuxiliaryAsync(
                    "subtype1",
                    () => SendSubtype1Async(session, result, honor));
                await SendExperienceAsync(session, result, honor, growthCapsule);
                if (sendQuestList)
                {
                    await TrySendAuxiliaryAsync(
                        "quest-list",
                        () => SendQuestListAsync(session));
                }
            }

            FileLogger.Log(
                $"[GameProtocol] EXPERIENCE_ITEM_SYNC: cid={session.Player.CharacterId} item={result.ItemTemplateId} level={result.PreviousLevel}->{result.NewLevel} exp={result.PreviousExp}->{result.NewExp} honor={result.HonorExpGain} inDungeon={inDungeon}");
        }

        private async Task SendSubtype0Async(
            EnhancedClientSession session,
            HonorLevelSummary honor)
        {
            var sent = await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                "EXPERIENCE_ITEM subtype0",
                honor);
            if (!sent)
                throw new InvalidOperationException(
                    $"subtype0 broadcast failed for character {session.Player.CharacterId}");
        }

        private async Task SendSubtype1Async(
            EnhancedClientSession session,
            ExperienceItemUseResult result,
            HonorLevelSummary honor)
        {
            var characterId = session.Player.CharacterId;
            var record = _characterRepository.GetById(characterId);
            var addition = _subtype1Repository.Load(characterId);
            if (record == null || addition == null || result.SyncedSkills == null)
            {
                throw new InvalidOperationException(
                    $"level-up subtype1 snapshot is unavailable for character {characterId}");
            }

            var accountCharacters = _characterRepository.ListByAccount(result.AccountId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0002,
                UserInfoBroadcastService.BuildSubtype1Body(
                    record, addition, accountCharacters, honor, result.SyncedSkills)));
        }

        private static Task SendExperienceAsync(
            EnhancedClientSession session,
            ExperienceItemUseResult result,
            HonorLevelSummary honor,
            GrowthCapsuleSummary growthCapsule)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EXP,
                ExpNotificationBuilder.Build(
                    result.NewLevel,
                    result.NewExp,
                    result.SkillPoints,
                    honor,
                    growthCapsuleExp: GrowthCapsuleDataProvider.GetDisplayProgress(
                        result.NewLevel,
                        growthCapsule))));

        private static async Task SendQuestListAsync(EnhancedClientSession session)
        {
            var questManager = session.GameSession?.QuestManager;
            if (questManager == null)
                throw new InvalidOperationException("quest manager is unavailable after level-up");
            await questManager.SendAcceptableQuestListAsync();
        }

        private static async Task TrySendAuxiliaryAsync(
            string name,
            Func<Task> send)
        {
            try
            {
                await send();
            }
            catch (Exception ex)
            {
                // 已提交的 EXP 快照是权威状态。派生刷新缺失时，
                // 不能阻止客户端继续消费后续 0x0025。
                FileLogger.Log(
                    $"[GameProtocol] EXPERIENCE_ITEM {name} refresh failed: {ex.Message}");
            }
        }

        private HonorLevelSummary ResolveHonor(
            ExperienceItemUseResult result,
            UserInfoMinimumTailSnapshot fallbackTail)
        {
            try
            {
                if (result.HonorExpGain > 0)
                {
                    return HonorLevelDataProvider.CalculateFromHonorExp(
                        result.TotalHonorExp,
                        _characterRepository.ListByAccount(result.AccountId));
                }

                return _honorLevel.LoadSummary(result.AccountId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GameProtocol] EXPERIENCE_ITEM honor snapshot failed: {ex.Message}");
                return new HonorLevelSummary
                {
                    HonorLevel = (byte)Math.Min(byte.MaxValue, fallbackTail?.ProgressA ?? 0),
                    HonorExp = fallbackTail?.ProgressB ?? 0,
                };
            }
        }

        private static void ApplyHonorToSession(
            EnhancedClientSession session,
            HonorLevelSummary honor)
        {
            var tail = session.Player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            HonorLevelDataProvider.ApplyToSubtype0Tail(tail, honor);
            session.Player.Subtype0Tail = tail;
        }
    }
}
