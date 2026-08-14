using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Mercenary;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class MercenaryHandler
    {
        private const ushort UserInfoNotiType = 0x0002;
        private const ushort SkillListCommand = 0x01E5;
        private const ushort SelectSkillCommand = 0x01E8;
        private const ushort TagCharacterInfoNotiType = 0x019F;

        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly SqliteMercenarySupportRepository _supportRepository;
        private readonly IGameDatabase _database;
        public string ProtocolName => "GameProtocol";

        public MercenaryHandler(
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _database = database ?? GameDatabase.CreateDefault();
            _honorLevel = new HonorLevelSyncService(_characterRepository, _database);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(_database);
            _supportRepository = new SqliteMercenarySupportRepository(_database);
        }

        public async Task HandleMercenaryRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var accountId = session.Account?.AccountId ?? 0;
            var activeCharacterId = session.Player?.CharacterId ?? 0;
            if (accountId <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER rejected: no authenticated account");
                var failureBody = header.type == SelectSkillCommand
                    ? BuildSelectFailureAck()
                    : header.type == SkillListCommand
                        ? BuildSkillListFailureAck()
                        : new byte[] { 0x00 };
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, failureBody));
                return;
            }
            var roster = ListAccountCharacters(accountId);

            if (header.type == SelectSkillCommand)
            {
                await HandleSelectSkill(session, header, body, activeCharacterId, roster);
                return;
            }

            if (header.type != SkillListCommand || body == null || body.Length < 2)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER skill list rejected: invalid command/body");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    BuildSkillListFailureAck()));
                return;
            }

            var requestEcho = ReadRequestEcho(body);
            var candidate = FindCandidateByWireIndex(roster, activeCharacterId, (byte)(requestEcho & 0xFF));
            var candidateInfo = BuildCandidateInfo(candidate);
            var listAck = BuildSkillListAck(candidateInfo, requestEcho);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, listAck));
            if (candidate != null)
            {
                var honorLevel = _honorLevel.LoadSummary(accountId, roster);
                await SendCandidateUserInfoAsync(session, candidate, honorLevel);
            }
        }

        private async Task HandleSelectSkill(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            int activeCharacterId,
            IReadOnlyList<CharacterRecord> roster)
        {
            if (body == null || body.Length < 3)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: body too short");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectFailureAck()));
                return;
            }

            var wireSlot = body[0];
            var slot = MercenarySupportState.SingletonStateKey;
            // 0x01E8 请求：wireSlot:u8 + skillId:u16；该 u16 不是 ComboIndex。
            var requestedSkillId = ReadUInt16(body, 1);

            if (activeCharacterId <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: no active character");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectFailureAck()));
                return;
            }

            if (requestedSkillId == 0)
            {
                await HandleClearSelectionAsync(session, header, activeCharacterId, slot);
                return;
            }

            var candidate = FindCandidateByWireIndex(roster, activeCharacterId, wireSlot);
            var selectedSkill = FindAvailableSkill(candidate, requestedSkillId);
            if (selectedSkill == null)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: wire={wireSlot} requestedSkill={requestedSkillId} is not available from current candidate");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectFailureAck()));
                return;
            }

            var state = new MercenarySupportState
            {
                OwnerCharacterId = activeCharacterId,
                Slot = slot,
                SupportCharacterId = candidate.CharacterId,
                SkillId = (ushort)selectedSkill.SkillIndex,
                StrikerSkillId = (ushort)selectedSkill.ComboIndex,
            };

            // 在持久化和 ACK 前验证可序列化性。
            var tagBody = StrikerSupportTagCharacterPacketBuilder.BuildOwnerMappedBody(
                activeCharacterId,
                state,
                _database);
            if (tagBody == null || tagBody.Length <= 2)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: dynamic 0x019F build failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectFailureAck()));
                return;
            }

            try
            {
                _supportRepository.Save(state);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select persist failed: {ex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectFailureAck()));
                return;
            }

            var ack = BuildSelectSuccessAck();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ack));
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                "MERCENARY/STRIKER select subtype0");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, TagCharacterInfoNotiType, tagBody));
        }

        private async Task HandleClearSelectionAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int activeCharacterId,
            byte slot)
        {
            try
            {
                _supportRepository.Clear(activeCharacterId, slot);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER clear selection failed: {ex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectFailureAck()));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildSelectSuccessAck()));
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                "MERCENARY/STRIKER clear selection subtype0");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                TagCharacterInfoNotiType,
                StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()));
        }

        private IReadOnlyList<CharacterRecord> ListAccountCharacters(int accountId)
        {
            return _characterRepository.ListByAccount(accountId);
        }

        internal static CharacterRecord FindCandidateByWireIndexForTest(IReadOnlyList<CharacterRecord> roster, int activeCharacterId, byte wireIndex)
        {
            return FindCandidateByWireIndex(roster, activeCharacterId, wireIndex);
        }

        private static CharacterRecord FindCandidateByWireIndex(IReadOnlyList<CharacterRecord> roster, int activeCharacterId, byte wireIndex)
        {
            if (roster == null || roster.Count == 0)
                return null;

            if (wireIndex >= roster.Count)
                return null;

            var candidate = roster[wireIndex];
            if (candidate == null
                || candidate.CharacterId <= 0
                || candidate.CharacterId > ushort.MaxValue
                || candidate.CharacterId == activeCharacterId
                || candidate.Level < StrikerSkillDataProvider.GetMinimumSupportLevel())
                return null;

            return candidate;
        }

        private StrikerCandidateInfo BuildCandidateInfo(CharacterRecord ch)
        {
            if (ch == null)
                return null;

            var learnedLevels = StrikerSupportSkillLevelSource.LoadLearnedLevels(ch.CharacterId);
            var skills = new List<StrikerCandidateSkillInfo>();

            foreach (var skill in StrikerSkillDataProvider.GetAvailableSkills(ch.Job, ch.GrowType, ch.Level))
            {
                var skillId = (ushort)skill.SkillIndex;
                learnedLevels.TryGetValue(skillId, out var level);
                skills.Add(new StrikerCandidateSkillInfo
                {
                    Skill = skill,
                    Level = level,
                });
            }

            return new StrikerCandidateInfo
            {
                Character = ch,
                Skills = skills,
            };
        }

        private static StrikerSkillEntry FindAvailableSkill(
            CharacterRecord candidate,
            ushort requestedSkillId)
        {
            if (candidate == null)
                return null;

            return StrikerSkillDataProvider.GetAvailableSkills(
                    candidate.Job,
                    candidate.GrowType,
                    candidate.Level)
                .FirstOrDefault(skill => skill.SkillIndex == requestedSkillId);
        }

        private static byte[] BuildSkillListAck(StrikerCandidateInfo candidateInfo, ushort requestValue)
        {
            // 0x01E5 成功包：success:u8, requestEcho:u16, reserved:u8*2, count:u8,
            // [reserved:u8, skillId:u16, learnedLevel:u8] * count；保留字固定为 0。
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt16(requestValue);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);

            var skills = candidateInfo?.Skills
                .Where(s => s.Level > 0)
                .ToList() ?? new List<StrikerCandidateSkillInfo>();

            writer.WriteByte((byte)Math.Min(byte.MaxValue, skills.Count));
            foreach (var skill in skills.Take(byte.MaxValue))
            {
                writer.WriteByte(0x00);
                writer.WriteUInt16((ushort)skill.Skill.SkillIndex);
                writer.WriteByte(skill.Level);
            }

            return writer.ToArray();
        }

        internal static byte[] BuildSelectSuccessAck()
        {
            // 0x01E8 成功包仅包含 result=1。
            return new byte[] { 0x01 };
        }

        internal static byte[] BuildSkillListFailureAck(byte errorCode = 0)
        {
            // 0x01E5 失败包包含 result=0 和 errorCode。
            return new byte[] { 0x00, errorCode };
        }

        internal static byte[] BuildSelectFailureAck(byte errorCode = 0)
        {
            // 0x01E8 失败包包含 result=0 和 errorCode。
            return new byte[] { 0x00, errorCode };
        }

        private async Task SendCandidateUserInfoAsync(
            EnhancedClientSession session,
            CharacterRecord character,
            HonorLevelSummary honorLevel)
        {
            if (character == null)
                return;

            try
            {
                character.Subtype0Tail = _subtype0Repository.Load(character.CharacterId);
                character.Appearance = Game.Appearance.AppearanceService.LoadCharacterAppearanceFromDb(
                    character,
                    _database);
                _honorLevel.ApplyToCharacterRecord(character, honorLevel);

                var writer = new GamePacketWriter();
                writer.WriteByte(0);
                writer.WriteUInt16(1);
                writer.WriteUInt16((ushort)character.CharacterId);
                writer.WriteDstr(character.Name);
                writer.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(character));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, UserInfoNotiType, writer.ToArray()));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER candidate subtype0 failed cid={character.CharacterId}: {ex.Message}");
            }
        }

        private static ushort ReadRequestEcho(byte[] body)
        {
            if (body != null && body.Length >= 2)
                return BitConverter.ToUInt16(body, 0);
            return 0;
        }

        private static ushort ReadUInt16(byte[] body, int offset)
        {
            return (ushort)(body[offset] | (body[offset + 1] << 8));
        }

        private sealed class StrikerCandidateInfo
        {
            public CharacterRecord Character { get; set; }
            public List<StrikerCandidateSkillInfo> Skills { get; set; } = new List<StrikerCandidateSkillInfo>();
        }

        private sealed class StrikerCandidateSkillInfo
        {
            public StrikerSkillEntry Skill { get; set; }
            public byte Level { get; set; }
        }

    }
}
