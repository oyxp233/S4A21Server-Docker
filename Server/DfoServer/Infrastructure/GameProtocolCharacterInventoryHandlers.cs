using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Infrastructure
{
    // 角色生命周期与库存入口的已装配 Handler 模块。
    internal sealed class GameProtocolCharacterInventoryHandlers
    {
        internal GameProtocolCharacterInventoryHandlers(
            LoginHandler login,
            CharacterSelectHandler characterSelect,
            InventoryHandler inventory,
            KnightShieldHandler knightShield)
        {
            Login = login ?? throw new ArgumentNullException(nameof(login));
            CharacterSelect = characterSelect
                ?? throw new ArgumentNullException(nameof(characterSelect));
            Inventory = inventory
                ?? throw new ArgumentNullException(nameof(inventory));
            KnightShield = knightShield
                ?? throw new ArgumentNullException(nameof(knightShield));
        }

        internal LoginHandler Login { get; }

        internal CharacterSelectHandler CharacterSelect { get; }

        internal InventoryHandler Inventory { get; }

        internal KnightShieldHandler KnightShield { get; }
    }
}
