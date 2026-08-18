using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    public class SkillHandler
    {
        private readonly ICharacterRepository _characterRepository;
        private readonly InventoryRefreshSender _refresh;
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;
        private readonly SqliteCharacterProgressRepository _progressRepository;
        private readonly SqliteSubtype1Repository _subtype1Repository;
        private readonly SqlitePvpSkillRepository _pvpSkillRepository;
        private readonly DarkKnightComboSkillService _darkKnightComboSkillService;

        public SkillHandler(
            ICharacterRepository characterRepository,
            InventoryRefreshSender refresh,
            IGameDatabase database = null,
            SqliteSelectCharacterDataSource selectCharacterDataSource = null)
        {
            _characterRepository = characterRepository
                ?? throw new ArgumentNullException(nameof(characterRepository));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            database ??= GameDatabase.CreateDefault();
            _selectCharacterDataSource = selectCharacterDataSource
                ?? new SqliteSelectCharacterDataSource(
                    database,
                    _characterRepository);
            _progressRepository = new SqliteCharacterProgressRepository(database);
            _subtype1Repository = new SqliteSubtype1Repository(database);
            _pvpSkillRepository = new SqlitePvpSkillRepository(database);
            _darkKnightComboSkillService = new DarkKnightComboSkillService(
                _progressRepository,
                new SqliteDarkKnightComboSkillRepository(database));
        }

        public async Task Handle_CHANGE_SKILL_COMMAND(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;

            var ack = new byte[body.Length + 1];
            ack[0] = 0x01;
            Buffer.BlockCopy(body, 0, ack, 1, body.Length);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x014B, ack));

            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid <= 0) return;

            var records = ParseSkillCommandRecords(body);
            if (records.Count == 0) return;

            try
            {
                foreach (var (page, skillId, commandBytes) in records)
                {
                    int rows;
                    if (GameNetworkConfig.IsFreeDuelListener(session.ListenerPort))
                    {
                        rows = _pvpSkillRepository
                            .UpdateSkillCommand(cid, skillId, commandBytes);
                    }
                    else
                    {
                        rows = _progressRepository.UpdateSkillCommand(
                            cid,
                            skillId,
                            commandBytes);
                    }
                    FileLogger.Log(
                        $"[SkillHandler] CHANGE_SKILL_COMMAND char={cid} page={page} skill={skillId} " +
                        $"cmd={BitConverter.ToString(commandBytes)} rows={rows}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SkillHandler] CHANGE_SKILL_COMMAND persist failed: {ex.Message}");
            }
        }

        public async Task Handle_RESET_ALL_SKILL_COMMANDS(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x014C, new byte[] { 0x01 }));

            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid <= 0) return;

            try
            {
                int cleared;
                if (GameNetworkConfig.IsFreeDuelListener(session.ListenerPort))
                {
                    cleared = _pvpSkillRepository.ClearAllSkillCommands(cid);
                }
                else
                {
                    cleared = _progressRepository.ClearAllSkillCommands(cid);
                }
                FileLogger.Log($"[SkillHandler] RESET_ALL_SKILL_COMMANDS char={cid} cleared={cleared}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SkillHandler] RESET_ALL_SKILL_COMMANDS failed: {ex.Message}");
            }
        }

        public async Task Handle_CHANGE_SKILLSLOT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3) return;
            var ack = new byte[] { 0x01, body[0], body[1], body[2] };
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001C, ack));

            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid > 0)
            {
                try
                {
                    int page = body[0] == 1 ? 1 : 0;
                    if (GameNetworkConfig.IsFreeDuelListener(session.ListenerPort))
                    {
                        _pvpSkillRepository.SwapSkillSlot(
                            cid,
                            page,
                            body[1],
                            body[2]);
                    }
                    else
                    {
                        if (session.Player?.Job == 9)
                            _darkKnightComboSkillService.SwapDarkKnightSkillSlot(
                                cid,
                                page,
                                body[1],
                                body[2]);
                        else
                            _progressRepository.SwapSkillSlot(
                                cid,
                                page,
                                body[1],
                                body[2]);
                    }
                }
                catch (Exception ex) { FileLogger.Log($"[SkillHandler] CHANGE_SKILLSLOT persist failed: {ex.Message}"); }
            }
        }

        public async Task Handle_COMBO_SKILL_INFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid <= 0 || body == null || body.Length == 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, 0x04 }));
                return;
            }

            if (session.Player.Job != 9)
            {
                FileLogger.Log($"[SkillHandler] COMBO_SKILL_INFO ignored non-dark-knight char={cid} job={session.Player.Job} len={body.Length}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, 0x04 }));
                return;
            }

            try
            {
                var isAutoComboCommit = ConsumePendingDarkKnightAutoCombo(session, cid);
                var result = isAutoComboCommit
                    ? _darkKnightComboSkillService.SaveAutoComboSkillInfo(cid, body)
                    : new Game.Skills.DarkKnightComboSkillSaveResult
                    {
                        Saved = _darkKnightComboSkillService.SaveComboSkillInfo(cid, body) > 0,
                    };
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                if (!result.Saved || result.QuickSlotsCleaned > 0)
                {
                    FileLogger.Log(
                        $"[SkillHandler] COMBO_SKILL_INFO char={cid} len={body.Length} " +
                        $"saved={result.Saved} auto={isAutoComboCommit} cleaned={result.QuickSlotsCleaned}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SkillHandler] COMBO_SKILL_INFO persist failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, 0x04 }));
            }
        }

        public async Task Handle_COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid > 0 && session.Player?.Job == 9)
                await TryRefreshDarkKnightSkillInfoBeforeAutoCombo(session, cid, body);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                CommonPacketBodyBuilder.BuildSuccessAck()));

            if (cid > 0 && session.Player?.Job == 9)
            {
                session.PendingDarkKnightAutoComboCharacterId = cid;
                session.PendingDarkKnightAutoComboUtc = DateTime.UtcNow;
            }
        }

        public async Task Handle_CHANGE_ANOTHER_SKILL_TREE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var currentSkillTreeIndex = ResolveClientCurrentSkillTreeIndex(session, body);
            var skillTreeIndex = currentSkillTreeIndex;
            int cid = session.Player != null ? session.Player.CharacterId : 0;

            if (cid > 0)
            {
                try
                {
                    currentSkillTreeIndex = _subtype1Repository.LoadSkillTreeIndex(cid)
                        ?? Game.Skills.SkillTreeExpansionState.LockedWireValue;
                    if (!Game.Skills.SkillTreeExpansionState.IsUnlocked(currentSkillTreeIndex))
                    {
                        var lockedTail = session.Player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
                        lockedTail.SkillTreeIndex = Game.Skills.SkillTreeExpansionState.LockedWireValue;
                        session.Player.Subtype0Tail = lockedTail;
                        FileLogger.Log($"[SkillHandler] CHANGE_ANOTHER_SKILL_TREE rejected: expansion locked char={cid}");
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                            0x01,
                            header.type,
                            BuildChangeAnotherSkillTreeAck(lockedTail.SkillTreeIndex, body)));
                        return;
                    }

                    skillTreeIndex = ToggleSkillTreeIndex(currentSkillTreeIndex);
                    var rows = _subtype1Repository.UpdateSkillTreeIndex(
                        cid,
                        skillTreeIndex);

                    var tail = session.Player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
                    tail.SkillTreeIndex = skillTreeIndex;
                    session.Player.Subtype0Tail = tail;

                    FileLogger.Log($"[SkillHandler] CHANGE_ANOTHER_SKILL_TREE char={cid} current={currentSkillTreeIndex} applied={skillTreeIndex} rows={rows} body={(body != null ? BitConverter.ToString(body) : "null")}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[SkillHandler] CHANGE_ANOTHER_SKILL_TREE persist failed: {ex.Message}");
                    skillTreeIndex = session.Player.Subtype0Tail?.SkillTreeIndex <= 1
                        ? session.Player.Subtype0Tail.SkillTreeIndex
                        : (byte)0;
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type,
                BuildChangeAnotherSkillTreeAck(skillTreeIndex, body)));
        }

        public async Task Handle_BUY_SKILL(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6) return;
            byte requestSkillTree = NormalizeSkillTreeIndexForBuy(body[0]);
            byte currentSkillTree = session?.Player?.Subtype0Tail?.SkillTreeIndex ?? requestSkillTree;
            byte skillTree = currentSkillTree <= 1 ? currentSkillTree : requestSkillTree;
            int count = body[1];
            var entries = new List<Game.Skills.BuySkillEntry>();
            for (int i = 0; i < count; i++)
            {
                int off = 2 + 4 * i;
                if (off + 3 >= body.Length) break;
                entries.Add(new Game.Skills.BuySkillEntry
                {
                    // 请求条目为 4 字节 {u16 skillIndex, u8 isRefund, u8 level}:
                    // 技能编号是双字节, 只读低位会把编号>255 的技能截断学错
                    // (实例: 战斗法师二觉被动 使徒封印, 见 PR589)。
                    SkillIndex = (ushort)(body[off] | (body[off + 1] << 8)),
                    IsRefund = body[off + 2],
                    Level = body[off + 3],
                });
            }

            int cid = session.Player != null ? session.Player.CharacterId : 0;
            int job = session.Player != null ? session.Player.Job : 0;
            if (cid > 0 && entries.Count > 0)
            {
                try
                {
                    var isPvpSkillChannel =
                        GameNetworkConfig.IsFreeDuelListener(session.ListenerPort);
                    if (!isPvpSkillChannel)
                    {
                        var storedSkillTree = _subtype1Repository.LoadSkillTreeIndex(cid)
                            ?? Game.Skills.SkillTreeExpansionState.LockedWireValue;
                        if (!Game.Skills.SkillTreeExpansionState.IsUnlocked(storedSkillTree)
                            && requestSkillTree == 1)
                        {
                            FileLogger.Log($"[SkillHandler] BUY_SKILL rejected: expansion locked char={cid} requestedTree={requestSkillTree}");
                            var rejected = new Game.Skills.BuySkillResult
                            {
                                Success = false,
                                SkillTree = requestSkillTree,
                                ErrorCode = 3,
                            };
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                                0x01, 0x001D, BuySkillAckBuilder.Build(rejected)));
                            return;
                        }

                        if (!Game.Skills.SkillTreeExpansionState.IsUnlocked(storedSkillTree))
                            skillTree = 0;
                    }

                    var rec = _characterRepository.GetById(cid);
                    // Account 缺失时传 0，避免误用账号 1 的契约效果。
                    Game.Skills.BuySkillResult result;
                    if (isPvpSkillChannel)
                    {
                        result = Game.Skills.BuySkillService.ExecutePvp(
                            _pvpSkillRepository,
                            cid,
                            session.Account?.AccountId ?? 0,
                            job,
                            skillTree,
                            entries,
                            rec?.BonusSp ?? 0,
                            rec?.Level ?? (byte)1,
                            rec?.BonusTp ?? 0,
                            rec?.GrowType ?? 0);
                    }
                    else
                    {
                        if (InventoryContext.TryGetLease(cid, out var lease) &&
                            lease.IsOwnedBy(session.SessionId))
                        {
                            result = Game.Skills.BuySkillService
                                .ExecuteWithRefundConsumable(
                                    lease,
                                    _progressRepository,
                                    cid,
                                    session.Account?.AccountId ?? 0,
                                    job,
                                    skillTree,
                                    entries,
                                    rec?.BonusSp ?? 0,
                                    rec?.Level ?? (byte)1,
                                    rec?.BonusTp ?? 0,
                                    rec?.GrowType ?? 0);
                        }
                        else
                        {
                            FileLogger.Log(
                                $"[SkillHandler] BUY_SKILL rejected: " +
                                $"online inventory missing char={cid}");
                            result = new Game.Skills.BuySkillResult
                            {
                                Success = false,
                                SkillTree = skillTree,
                                ErrorCode = 3,
                            };
                        }
                    }

                    var ack = BuySkillAckBuilder.Build(result);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001D, ack));
                    if (result != null &&
                        result.Success &&
                        result.ConsumedForgetRiverWater &&
                        result.ConsumedForgetRiverWaterItem != null)
                    {
                        await _refresh.SendUpdateItemList(
                            session,
                            result.ConsumedForgetRiverWaterItem.ListType,
                            result.ConsumedForgetRiverWaterItem.SlotIndex);
                    }
                }
                catch (Exception ex) { FileLogger.Log($"[SkillHandler] BUY_SKILL failed: {ex}"); }
            }
        }

        public async Task Handle_SKILL_INIT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid <= 0) return;

            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x01, 0x00 }));

                var accountId = session.Account?.AccountId ?? 1;
                _selectCharacterDataSource.PrepareForSkillSynchronization(
                    cid,
                    accountId);
                var snapshot = _selectCharacterDataSource.Load(cid, accountId);
                var skillInfo = snapshot.InitializationSnapshot.SkillInfo;
                if (GameNetworkConfig.IsFreeDuelListener(session.ListenerPort))
                {
                    var character = _characterRepository.GetById(cid);
                    if (character != null)
                    {
                        skillInfo = _pvpSkillRepository.LoadOrInitialize(
                            cid,
                            character.Job,
                            character.Level,
                            character.GrowType);
                    }
                }
                var skillBytes = SkillInfoBodyBuilder.BuildFrom(skillInfo);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.SKILLINFO,
                    skillBytes));

                FileLogger.Log($"[SkillHandler] SKILL_INIT refresh char={cid}");
            }
            catch (Exception ex) { FileLogger.Log($"[SkillHandler] SKILL_INIT failed: {ex}"); }
        }

        private static List<(int page, ushort skillId, byte[] commandBytes)> ParseSkillCommandRecords(byte[] body)
        {
            var records = new List<(int, ushort, byte[])>();
            if (body == null || body.Length < 4) return records;

            int page = body[0] == 1 ? 1 : 0;
            int offset = 0;
            bool first = true;

            while (offset < body.Length)
            {
                int headerSize = first ? 4 : 3;
                if (offset + headerSize > body.Length) break;

                ushort skillId = first ? body[offset + 1] : body[offset];
                int lenPos = first ? offset + 2 : offset + 1;
                int cmdLen = (body[lenPos] << 8) | body[lenPos + 1];
                offset += headerSize;

                if (!first && cmdLen <= 0) break;
                if (offset + cmdLen > body.Length) break;

                var commandBytes = new byte[cmdLen];
                Buffer.BlockCopy(body, offset, commandBytes, 0, cmdLen);
                records.Add((page, skillId, commandBytes));
                offset += cmdLen;
                first = false;
            }

            return records;
        }

        private static byte[] BuildChangeAnotherSkillTreeAck(byte skillTreeIndex, byte[] body)
        {
            if (body == null || body.Length == 0)
                return new[] { (byte)0x01, skillTreeIndex };

            var ack = new byte[body.Length + 1];
            ack[0] = 0x01;
            Buffer.BlockCopy(body, 0, ack, 1, body.Length);
            ack[1] = skillTreeIndex;
            return ack;
        }

        private static byte ResolveClientCurrentSkillTreeIndex(EnhancedClientSession session, byte[] body)
        {
            if (body != null && body.Length == 1)
            {
                byte value;
                if (TryNormalizeSkillTreeIndex(body[0], out value))
                    return value;
            }

            if (body != null && body.Length == 2)
            {
                byte value;
                if (TryNormalizeSkillTreeIndex(body[1], out value))
                    return value;
            }

            var current = session?.Player?.Subtype0Tail?.SkillTreeIndex ?? 0;
            return current <= 1 ? current : (byte)0;
        }

        private static byte ToggleSkillTreeIndex(byte currentSkillTreeIndex)
        {
            return currentSkillTreeIndex == 0 ? (byte)1 : (byte)0;
        }

        private static bool TryNormalizeSkillTreeIndex(byte raw, out byte value)
        {
            if (raw <= 1)
            {
                value = raw;
                return true;
            }

            if (raw == 2)
            {
                value = 1;
                return true;
            }

            value = 0;
            return false;
        }

        private static byte NormalizeSkillTreeIndexForBuy(byte raw)
        {
            byte value;
            return TryNormalizeSkillTreeIndex(raw, out value) ? value : (byte)0;
        }

        private async Task TryRefreshDarkKnightSkillInfoBeforeAutoCombo(
            EnhancedClientSession session,
            int characterId,
            byte[] requestBody)
        {
            try
            {
                var page = requestBody != null && requestBody.Length > 0 && requestBody[0] == 1 ? 1 : 0;
                var cleanup = _darkKnightComboSkillService
                    .CleanDuplicateQuickSlots(characterId, page);
                if (cleanup.QuickSlotsCleaned <= 0)
                    return;

                var skills = _progressRepository.LoadSkills(characterId);
                var skillBytes = SkillInfoBodyBuilder.BuildFrom(skills);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.SKILLINFO,
                    skillBytes));
                FileLogger.Log(
                    $"[SkillHandler] COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET pre-clean " +
                    $"char={characterId} page={page} cleaned={cleanup.QuickSlotsCleaned} skillInfoLen={skillBytes.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SkillHandler] COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET pre-clean failed: {ex.Message}");
            }
        }

        private static bool ConsumePendingDarkKnightAutoCombo(EnhancedClientSession session, int characterId)
        {
            if (session == null || characterId <= 0)
                return false;

            var matches = session.PendingDarkKnightAutoComboCharacterId == characterId
                && session.PendingDarkKnightAutoComboUtc > DateTime.MinValue
                && DateTime.UtcNow - session.PendingDarkKnightAutoComboUtc <= TimeSpan.FromSeconds(10);

            if (matches || session.PendingDarkKnightAutoComboCharacterId == characterId)
            {
                session.PendingDarkKnightAutoComboCharacterId = 0;
                session.PendingDarkKnightAutoComboUtc = DateTime.MinValue;
            }

            return matches;
        }
    }
}
