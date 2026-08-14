using System;
using DfoServer.Game.Quests;

namespace DfoServer.Network.Parsers.Quest
{
    internal static class QuestCommandParser
    {
        private const int FinishBodyLength = 8;

        internal static bool TryParseImageCommunicationUse(
            byte[] body,
            out ImageCommunicationUseCommand command)
        {
            command = default;
            return body == null || body.Length == 0;
        }

        internal static bool TryParseAccept(
            byte[] body,
            out QuestAcceptCommand command)
        {
            command = default;
            if (body == null || body.Length < 2)
                return false;
            command = new QuestAcceptCommand(BitConverter.ToUInt16(body, 0));
            return true;
        }

        internal static bool TryParseGiveup(
            byte[] body,
            out QuestGiveupCommand command)
        {
            command = default;
            if (body == null || body.Length < 2)
                return false;
            command = new QuestGiveupCommand(BitConverter.ToUInt16(body, 0));
            return true;
        }

        internal static bool TryParseFinish(
            byte[] body,
            out QuestFinishCommand command)
        {
            command = default;
            if (body == null || body.Length != FinishBodyLength)
                return false;

            var questId = BitConverter.ToUInt16(body, 0);
            var rewardSelection = BitConverter.ToUInt16(body, 2);
            var completionCount = BitConverter.ToUInt16(body, 4);
            var sentinel = BitConverter.ToUInt16(body, 6);
            if (sentinel != ushort.MaxValue)
                return false;

            command = new QuestFinishCommand(
                questId,
                rewardSelection != ushort.MaxValue,
                rewardSelection,
                completionCount);
            return true;
        }

        internal static bool TryParseSaveNotify(
            byte[] body,
            out QuestNotifySelectionCommand command)
        {
            command = default;
            if (body == null || body.Length < 1)
                return false;

            var count = body[0];
            if (count > QuestNotifySelectionService.MaxSlots
                || body.Length != 1 + count * sizeof(int))
            {
                return false;
            }

            var questIds = new int[count];
            for (var index = 0; index < count; index++)
            {
                var questId = BitConverter.ToInt32(body, 1 + index * sizeof(int));
                if (questId <= 0 || Array.IndexOf(questIds, questId, 0, index) >= 0)
                    return false;
                questIds[index] = questId;
            }

            command = new QuestNotifySelectionCommand(questIds);
            return true;
        }
    }
}
