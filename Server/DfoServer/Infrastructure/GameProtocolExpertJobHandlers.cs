using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Infrastructure
{
    // 专家职业协议入口模块；共享状态与事务依赖由组合根统一提供。
    internal sealed class GameProtocolExpertJobHandlers
    {
        internal GameProtocolExpertJobHandlers(
            ExpertJobStoreHandler store,
            ExpertJobExtractionHandler extraction,
            ExpertJobCompoundHandler compound,
            ExpertJobGiveupHandler giveup,
            EnchanterHandler enchanter)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Extraction = extraction
                ?? throw new ArgumentNullException(nameof(extraction));
            Compound = compound
                ?? throw new ArgumentNullException(nameof(compound));
            Giveup = giveup ?? throw new ArgumentNullException(nameof(giveup));
            Enchanter = enchanter
                ?? throw new ArgumentNullException(nameof(enchanter));
        }

        internal ExpertJobStoreHandler Store { get; }

        internal ExpertJobExtractionHandler Extraction { get; }

        internal ExpertJobCompoundHandler Compound { get; }

        internal ExpertJobGiveupHandler Giveup { get; }

        internal EnchanterHandler Enchanter { get; }
    }
}
