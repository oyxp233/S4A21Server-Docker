using System;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;

namespace DfoServer.Network.Handlers
{
    internal sealed class ExpertJobExtractionHandler
    {
        private const ushort ExtractionCommand = (ushort)CmdPacketType.EXPERT_EXTRACTION;

        private readonly IExpertJobStateRepository _states;
        private readonly ICharacterRepository _characters;
        private readonly SqliteSubtype0FieldsRepository _subtype0;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly ExpertJobPersistenceService _persistence;
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly ExpertJobOperationCoordinator _operations;

        internal ExpertJobExtractionHandler(
            IExpertJobStateRepository states,
            ICharacterRepository characters,
            SqliteSubtype0FieldsRepository subtype0,
            HonorLevelSyncService honorLevel,
            ExpertJobPersistenceService persistence,
            InventoryRefreshSender inventoryRefresh,
            ExpertJobOperationCoordinator operations)
        {
            _states = states ?? throw new ArgumentNullException(nameof(states));
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _subtype0 = subtype0 ?? throw new ArgumentNullException(nameof(subtype0));
            _honorLevel = honorLevel ?? throw new ArgumentNullException(nameof(honorLevel));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _inventoryRefresh = inventoryRefresh ?? throw new ArgumentNullException(nameof(inventoryRefresh));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        internal async Task Handle(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!ExpertJobExtractionRequest.TryParse(body, out var command)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != command.ExtractorType
                || !ExpertJobConfigRegistry.TryGetExtractionConfig(
                    command.ExtractorType,
                    out var config)
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendError(session, ExpertJobExtractionService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            try
            {
                ExpertJobExtractionResult result;
                bool success;
                var previousExperience = player.Subtype0Tail.ExpertJobExp;
                lock (lease.SyncRoot)
                {
                    success = ExpertJobExtractionService.TryExtract(
                        lease.Inventory,
                        command,
                        previousExperience,
                        config,
                        out result);
                }
                if (!success)
                {
                    await SendError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _states.SaveProgressInTransaction(
                            connection,
                            transaction,
                            player.CharacterId,
                            result.ExperienceGain,
                            result.LearnedRecipeIds)))
                {
                    await SendError(session, ExpertJobExtractionService.ErrorInvalidState);
                    return;
                }

                player.Subtype0Tail.ExpertJobExp = result.FinalExperience;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    ExtractionCommand,
                    ExpertJobExtractionPacketBuilder.BuildSuccess(result)));
                await _inventoryRefresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    BuildRefreshSlots(result));
                await UserInfoBroadcastService.SendSubtype0Async(
                    session,
                    _characters,
                    _subtype0,
                    _honorLevel,
                    "EXPERT_JOB_EXP_REFRESH");
                if (config.RecipeConfig.GetLevel(previousExperience)
                    != config.RecipeConfig.GetLevel(result.FinalExperience))
                {
                    var state = _states.Load(player.CharacterId, command.ExtractorType);
                    var expertJobBody = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                        command.ExtractorType,
                        state,
                        result.FinalExperience);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x00CD,
                        expertJobBody));
                }
                var questManager = session.GameSession?.QuestManager;
                if (questManager != null)
                {
                    await questManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                        lease,
                        result.InventoryMutations);
                }
                FileLogger.Log(
                    $"[ExpertJob] EXTRACT cid={player.CharacterId} " +
                    $"type={command.ExtractorType} extractorSlot={command.ExtractorSlotIndex} " +
                    $"targetSlot={command.TargetSlotIndex} results={result.Materials.Count} " +
                    $"exp={result.ExperienceGain}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        private static Task SendError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                ExtractionCommand,
                CommonPacketBodyBuilder.BuildCmdError(errorCode)));

        private static short[] BuildRefreshSlots(ExpertJobExtractionResult result)
        {
            var slots = new short[result.Materials.Count + 1];
            slots[0] = result.TargetSlotIndex;
            for (var index = 0; index < result.Materials.Count; index++)
                slots[index + 1] = result.Materials[index].SlotIndex;
            return slots;
        }
    }
}
