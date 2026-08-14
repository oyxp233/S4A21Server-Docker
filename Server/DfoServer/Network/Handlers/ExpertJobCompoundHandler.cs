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
    internal sealed class ExpertJobCompoundHandler
    {
        private const ushort CompoundCommand =
            (ushort)CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB;

        private readonly ExpertJobStoreRuntimeService _stores;
        private readonly IExpertJobStateRepository _states;
        private readonly ICharacterRepository _characters;
        private readonly SqliteSubtype0FieldsRepository _subtype0;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly ExpertJobPersistenceService _persistence;
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly ExpertJobOperationCoordinator _operations;

        internal ExpertJobCompoundHandler(
            ExpertJobStoreRuntimeService stores,
            IExpertJobStateRepository states,
            ICharacterRepository characters,
            SqliteSubtype0FieldsRepository subtype0,
            HonorLevelSyncService honorLevel,
            ExpertJobPersistenceService persistence,
            InventoryRefreshSender inventoryRefresh,
            ExpertJobOperationCoordinator operations)
        {
            _stores = stores ?? throw new ArgumentNullException(nameof(stores));
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
            var expertJobType = player?.Subtype0Tail?.ExpertJobType ?? 0;
            if (!ExpertJobCompoundRequest.TryParse(body, out var command)
                || player == null
                || player.CurrentRun != null
                || !ExpertJobConfigRegistry.TryGetRecipeConfig(expertJobType, out var recipeConfig)
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendError(session, ExpertJobCompoundService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            var responded = false;
            try
            {
                if (_stores.HasStore(player.CharacterId))
                {
                    await SendError(session, ExpertJobCompoundService.ErrorInvalidState);
                    return;
                }

                var previousExperience = player.Subtype0Tail.ExpertJobExp;
                ExpertJobConfigRegistry.TryGetExtractionConfig(
                    expertJobType,
                    out var extractionConfig);
                var state = command.IsProductCraft
                    ? _states.Load(player.CharacterId, expertJobType)
                    : null;
                ExpertJobCompoundResult result = null;
                bool success;
                lock (lease.SyncRoot)
                {
                    success = command.IsProductCraft
                        ? ExpertJobCompoundService.TryCraftProduct(
                            lease.Inventory,
                            command,
                            previousExperience,
                            state,
                            recipeConfig,
                            extractionConfig,
                            out result)
                        : expertJobType == ExpertJobStateCodec.EnchanterType
                            && EnchanterCompoundService.TryCraftBead(
                                lease.Inventory,
                                command,
                                previousExperience,
                                out result);
                }
                if (!success)
                {
                    await SendError(session, result?.ErrorCode
                        ?? ExpertJobCompoundService.ErrorInvalidState);
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
                    await SendError(session, ExpertJobCompoundService.ErrorInvalidState);
                    return;
                }

                player.Subtype0Tail.ExpertJobExp = result.FinalExperience;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    CompoundCommand,
                    ExpertJobCompoundPacketBuilder.BuildSuccess(result)));
                responded = true;
                if (result.ExtractorInventoryChanged)
                {
                    await _inventoryRefresh.SendItemListRefresh(
                        session,
                        InventoryListType.Main);
                }
                else
                {
                    await _inventoryRefresh.SendUpdateItemList(
                        session,
                        InventoryListType.Main,
                        result.ChangedMainSlots);
                }
                if (result.GoldSpent > 0 && !result.ExtractorInventoryChanged)
                    await _inventoryRefresh.SendGoldUpdate(session);
                if (result.ExperienceGain > 0)
                {
                    await UserInfoBroadcastService.SendSubtype0Async(
                        session,
                        _characters,
                        _subtype0,
                        _honorLevel,
                        "EXPERT_JOB_EXP_REFRESH");
                }
                if (result.RequiresExpertJobInfoRefresh
                    || result.ExtractorInventoryChanged)
                {
                    var refreshedState = _states.Load(player.CharacterId, expertJobType);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x00CD,
                        ExpertJobInfoBodyBuilder.BuildProjectedBody(
                            expertJobType,
                            refreshedState,
                            result.FinalExperience)));
                }

                FileLogger.Log(
                    $"[ExpertJob] COMPOUND cid={player.CharacterId} type={expertJobType} " +
                    $"kind={(command.IsProductCraft ? "product" : "bead")} " +
                    $"recipe={command.RecipeItemId} count={command.RequestedCount} " +
                    $"cardSlot={command.CardSlotIndex} outputs={result.Outputs.Count} " +
                    $"failure={result.FailureCount} exp={result.ExperienceGain}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJob] COMPOUND failed cid={player.CharacterId}: {ex.Message}");
                if (!responded)
                    await SendError(session, ExpertJobCompoundService.ErrorInvalidState);
            }
            finally
            {
                operationGate.Release();
            }
        }

        private static Task SendError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                CompoundCommand,
                ExpertJobCompoundPacketBuilder.BuildError(errorCode)));
    }
}
