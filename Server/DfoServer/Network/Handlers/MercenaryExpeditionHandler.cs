using DfoServer.Game.Mercenary;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class MercenaryExpeditionHandler
    {
        public const ushort ReturnCommand = 0x01B9;
        public const ushort InfoCommand = 0x01BA;
        public const ushort CompetitionCommand = 0x01BB;
        internal const byte CompetitionErrorCode = 21;

        private readonly MercenaryService _service;

        public MercenaryExpeditionHandler(MercenaryService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public async Task HandleInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null)
                body = Array.Empty<byte>();
            if (body.Length != 0 || session?.Account == null)
            {
                FileLogger.Log($"[Mercenary] INFO rejected body={body.Length} authenticated={session?.Account != null}");
                if (session != null)
                    await Send(session, InfoCommand, MercenaryExpeditionBodyBuilder.BuildError(1));
                return;
            }

            var snapshot = _service.GetInfo(session.Account.AccountId);
            await Send(session, InfoCommand, MercenaryExpeditionBodyBuilder.BuildInfoSuccess(snapshot));
            FileLogger.Log(
                $"[Mercenary] INFO account={session.Account.AccountId} level={snapshot.ManageLevel} "
                + $"point={snapshot.ManagePoint} records={snapshot.Records.Count}");
        }

        public async Task HandleReturn(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length != 5 || session?.Account == null)
            {
                FileLogger.Log($"[Mercenary] RETURN rejected body={body?.Length ?? 0} authenticated={session?.Account != null}");
                if (session != null)
                    await Send(session, ReturnCommand, MercenaryExpeditionBodyBuilder.BuildError(7));
                return;
            }

            var purpose = body[0];
            var characterId = BitConverter.ToInt32(body, 1);
            var result = _service.Return(session.Account.AccountId, characterId, purpose);
            var response = result.Success
                ? MercenaryExpeditionBodyBuilder.BuildReturnSuccess(
                    characterId,
                    result.Purpose,
                    result.Reward?.ItemTemplateId ?? 0,
                    result.Reward?.ItemCount ?? 0,
                    (result.Reward?.CompletedHours ?? 0) > 0)
                : MercenaryExpeditionBodyBuilder.BuildError(7);
            await Send(session, ReturnCommand, response);
            if (result.Success)
                await SendInfo(session, "return");
            if (!result.Success)
                FileLogger.Log($"[Mercenary] RETURN failed account={session.Account.AccountId} char={characterId} status={result.Status}");
        }

        public async Task HandleCompetition(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length != 6 || session?.Account == null)
            {
                FileLogger.Log($"[Mercenary] COMPETITION rejected body={body?.Length ?? 0} authenticated={session?.Account != null}");
                if (session != null)
                    await Send(session, CompetitionCommand, MercenaryExpeditionBodyBuilder.BuildError(CompetitionErrorCode));
                return;
            }

            var characterId = BitConverter.ToInt32(body, 0);
            var areaIndex = body[4];
            var periodIndex = body[5];
            var activeCharacterId = session.Player?.CharacterId ?? 0;
            var result = _service.Dispatch(
                session.Account.AccountId,
                activeCharacterId,
                characterId,
                areaIndex,
                periodIndex);

            var response = result.Success
                ? MercenaryExpeditionBodyBuilder.BuildCompetitionSuccess(
                    result.Assignment.CharacterId,
                    result.Assignment.AreaIndex,
                    result.Assignment.PeriodIndex)
                : MercenaryExpeditionBodyBuilder.BuildError(CompetitionErrorCode);
            await Send(session, CompetitionCommand, response);
            if (result.Success)
                await SendInfo(session, "competition");
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Mercenary] COMPETITION failed account={session.Account.AccountId} active={activeCharacterId} "
                    + $"char={characterId} area={areaIndex} period={periodIndex} status={result.Status}");
            }
        }

        private static Task Send(EnhancedClientSession session, ushort command, byte[] body)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, command, body));

        private async Task SendInfo(EnhancedClientSession session, string reason)
        {
            var snapshot = _service.GetInfo(session.Account.AccountId);
            await Send(session, InfoCommand, MercenaryExpeditionBodyBuilder.BuildInfoSuccess(snapshot));
            FileLogger.Log(
                $"[Mercenary] INFO refresh reason={reason} account={session.Account.AccountId} "
                + $"level={snapshot.ManageLevel} point={snapshot.ManagePoint} records={snapshot.Records.Count}");
        }
    }
}
