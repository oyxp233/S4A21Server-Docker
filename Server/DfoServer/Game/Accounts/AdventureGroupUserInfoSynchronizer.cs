using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using System.Collections.Generic;

namespace DfoServer.Game.Accounts
{
    public static class AdventureGroupUserInfoSynchronizer
    {
        public static AdventureGroupSummary ApplyToUserInfoAddition(
            UserInfoAdditionSnapshot addition,
            IEnumerable<CharacterRecord> accountCharacters)
        {
            var summary = AdventureGroupDataProvider.Calculate(accountCharacters);
            ApplyToUserInfoAddition(addition, summary);
            return summary;
        }

        public static void ApplyToUserInfoAddition(
            UserInfoAdditionSnapshot addition,
            AdventureGroupSummary summary)
        {
            if (addition == null || summary == null)
                return;

            // 冒险团等级驱动图标；尾部字节触发客户端四维额外加成。
            addition.ManageLevel = summary.ManageLevel;
            addition.FlagByte = summary.ManageLevel;
        }
    }
}
