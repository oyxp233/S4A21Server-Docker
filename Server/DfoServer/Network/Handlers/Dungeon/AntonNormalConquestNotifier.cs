using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Compatibility facade: validates the current session/run context, then
    // delegates state transitions and wire projection to their owners.
    internal sealed class AntonNormalConquestNotifier
    {
        private readonly AntonNormalConquestApplicationService _application;
        private readonly AntonNormalConquestNotificationSender _sender;

        internal AntonNormalConquestNotifier(
            SqliteCharacterStateRepository repository,
            AntonAwakeningDailyProgressService awakeningProgress = null)
        {
            _application = new AntonNormalConquestApplicationService(
                repository,
                awakeningProgress);
            _sender = new AntonNormalConquestNotificationSender();
        }

        internal void ConfigureLinkedChallenge(DungeonRun run)
            => _application.ConfigureLinkedChallenge(run);

        // Answer to CMD SEQUENTIAL_DUNGEON_INFO (0x035D): resolve the
        // persisted progress of the sequence the client asked about.
        // Unknown keys and sequences without progress report 0 (not started).
        internal byte ResolveSequentialProgress(int characterId, int configKey)
        {
            return TryResolveSequentialState(
                    characterId,
                    configKey,
                    out var state)
                ? state.ProgressIndex
                : (byte)0;
        }

        internal bool TryResolveSequentialState(
            int characterId,
            int configKey,
            out AntonNormalSyncState state)
        {
            state = null;
            return characterId > 0
                && _application.TryRestore(characterId, configKey, out state);
        }

        internal async Task RestoreBeforeSelectAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;
            var expectedTownGeneration =
                session.Player.CurrentDungeonRunGeneration;
            if (session.Player.CurrentRun != null)
                return;

            try
            {
                // 只恢复玩家当前所在副本入口区域的序列状态。此前无条件推送
                // 安徒恩序列(key=28/41), 镇魂(area 26)等其他区域的客户端收到
                // 错区域数据后会追问自身区域的序列(CMD 0x035D)。
                if (!Town.TryGetDungeonGateReturnInfo(
                        session.Player.CurTownId,
                        session.Player.CurAreaId,
                        out var gate)
                    || gate.WorldMapAreaId <= 0)
                {
                    return;
                }
                if (_application.TryRestore(
                        session.Player.CharacterId,
                        gate.WorldMapAreaId,
                        out var state))
                {
                    await _sender.SendAsync(
                        session,
                        state,
                        "enter-select-dungeon",
                        expectedRun: null,
                        expectedTownGeneration);
                }

                // 暴走安徒恩(序列 41)和普通安徒恩(序列 28)共用同一个入口
                // 城镇, 所以 gate.WorldMapAreaId 恒为 28, 序列 41 永远收不到
                // 主动推送——队长在暴走模式里点 243 之前, 客户端手上没有暴走
                // 进度, 组队时就会被本地判成「与组队模式不符」。这里在入口
                // 区域本身属于安徒恩序列时补推一次暴走序列。
                if (!TryResolveAwakeningCompanionKey(
                        gate.WorldMapAreaId,
                        out var awakeningKey))
                {
                    return;
                }
                if (!_application.TryRestore(
                        session.Player.CharacterId,
                        awakeningKey,
                        out var awakeningState))
                {
                    return;
                }
                await _sender.SendAsync(
                    session,
                    awakeningState,
                    "enter-select-dungeon-awakening",
                    expectedRun: null,
                    expectedTownGeneration);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] restore skipped: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }

        // 玩家站在安徒恩系列副本入口(普通安徒恩区域 28)时, 返回需要一并
        // 恢复的暴走序列 key。入口区域本身不是安徒恩序列时返回 false,
        // 免得镇魂(区域 26)之类的客户端又收到错区域数据。
        private static bool TryResolveAwakeningCompanionKey(
            int gateWorldMapAreaId,
            out int awakeningKey)
        {
            awakeningKey = 0;
            if (gateWorldMapAreaId <= 0)
                return false;

            var catalog = SequentialDungeonDefinitionCatalog.Current;
            if (!catalog.TryGetByGroupKey(
                    gateWorldMapAreaId,
                    out var entranceDefinition)
                || entranceDefinition == null
                || !entranceDefinition.IsAntonDungeonSequence)
            {
                return false;
            }
            if (!catalog.TryResolveAntonAwakeningDefinition(
                    out var awakeningDefinition)
                || awakeningDefinition.GroupKey == gateWorldMapAreaId)
            {
                return false;
            }

            awakeningKey = awakeningDefinition.GroupKey;
            return true;
        }

        internal async Task ApplyClearAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || run == null)
            {
                return;
            }

            try
            {
                if (!_application.TryApplyClear(
                        session.Player.CharacterId,
                        run.DungeonId,
                        out var result))
                {
                    return;
                }
                await _sender.SendAsync(
                    session,
                    result.State,
                    "dungeon-clear",
                    run.CaptureIdentity(),
                    expectedTownGeneration: null);
                FileLogger.Log(
                    $"[AntonNormal] clear applied: dungeon={run.DungeonId} " +
                    $"changes={(result.Changes.Count == 0
                        ? "none"
                        : string.Join(",", result.Changes.Select(
                            entry => $"{entry.DungeonId}:{entry.ClearState}")))} " +
                    $"progress={result.State.ProgressIndex} " +
                    $"routeMask=0x{result.State.RouteMask:X2}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] clear sync failed and remains retryable: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"error={ex.Message}");
                throw;
            }
        }
    }
}
