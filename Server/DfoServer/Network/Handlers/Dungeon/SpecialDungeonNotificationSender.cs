using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal interface ISpecialDungeonNotificationSender
    {
        Task SendAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect);
    }

    internal sealed class SpecialDungeonNotificationSender
        : ISpecialDungeonNotificationSender
    {
        private const byte SummonMonsterResult = 0x01;
        private const byte SummonMonsterMode = 0x03;
        private const byte StrongWarlordResult = 0x01;

        Task ISpecialDungeonNotificationSender.SendAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect)
            => SendAsync(session, effect);

        internal async Task SendAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect)
        {
            if (session == null || effect == null)
                return;

            switch (effect.Kind)
            {
                case SpecialDungeonEffectKind.GaugeChanged:
                    await SendGaugeAsync(session, effect.Value);
                    return;

                case SpecialDungeonEffectKind.BuffAddedAndActivated:
                    await DungeonBuffNotificationSender
                        .SendAddedAndActivateAsync(
                            session,
                            effect.BuffIds,
                            effect.ActiveBuffIds);
                    return;

                case SpecialDungeonEffectKind.BuffsCleared:
                    await DungeonBuffNotificationSender.ClearAsync(
                        session,
                        effect.BuffIds);
                    return;

                case SpecialDungeonEffectKind.BossEntranceMinimap:
                    await SendMinimapAsync(session, effect);
                    return;

                case SpecialDungeonEffectKind.PassGate:
                    await DungeonMechanismNotificationSender
                        .SendCompleteConditionPassGateAsync(
                            session,
                            "ordinary-special-dungeon",
                            effect.Reason);
                    return;

                case SpecialDungeonEffectKind.StrongWarlordSelected:
                    await session.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(
                            0x01,
                            (ushort)CmdPacketType.TIMER_MODIFY_INFO,
                            new[] { StrongWarlordResult }));
                    return;

                case SpecialDungeonEffectKind.SummonMonsterResponse:
                    await SendSummonMonsterAsync(session, effect);
                    return;

                case SpecialDungeonEffectKind.CommandSuccessAck:
                    await session.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(
                            0x01,
                            effect.WireType,
                            CommonPacketBodyBuilder.BuildSuccessAck()));
                    return;
            }
        }

        private static Task SendGaugeAsync(
            EnhancedClientSession session,
            int value)
        {
            var body = SpecialDungeonNotificationBuilder
                .BuildGaugeObjectBarData(value);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.GAUGE_OBJECT_BAR_DATA,
                body));
        }

        private static Task SendMinimapAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect)
        {
            var body = SpecialDungeonNotificationBuilder.BuildMinimapIconInfo(
                effect.MinimapEntries);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.MINIMAP_ICON_INFO,
                body));
        }

        private static Task SendSummonMonsterAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect)
        {
            var body = SpecialDungeonNotificationBuilder
                .BuildSummonMonsterCommandCreateResponse(
                    SummonMonsterResult,
                    effect.StateId,
                    1,
                    SpecialDungeonMechanismApplicationService
                        .BossSummonRuntimeKey,
                    effect.MonsterCode,
                    SummonMonsterMode,
                    effect.MonsterLevel);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.SUMMON_MONSTER,
                body));
        }
    }
}
