using System;
using DfoServer.Game.Quests;
using DfoServer.Network.Parsers.Quest;

namespace DfoServer.SelfTests
{
    internal static class QuestSelfTestCommandAdapter
    {
        internal static QuestFinishResult HandleFinish(
            QuestService service,
            int characterId,
            byte[] body,
            uint? currentExp = null)
        {
            if (service == null
                || !QuestCommandParser.TryParseFinish(body, out var command))
            {
                return QuestFinishResult.Fail(22);
            }

            return service.HandleFinishQuest(characterId, command, currentExp);
        }

        internal static byte[] BuildFinishBody(
            ushort questId,
            ushort rewardSelection = ushort.MaxValue,
            ushort completionCount = 1,
            ushort sentinel = ushort.MaxValue)
        {
            var body = new byte[8];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            BitConverter.GetBytes(rewardSelection).CopyTo(body, 2);
            BitConverter.GetBytes(completionCount).CopyTo(body, 4);
            BitConverter.GetBytes(sentinel).CopyTo(body, 6);
            return body;
        }
    }
}
