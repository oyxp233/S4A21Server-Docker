using DfoServer.Game.Inventory;

namespace DfoServer.Network
{
    internal static class GameChannelTeleportPolicy
    {
        internal static bool CanUseConsumable(
            int listenerGamePort,
            TeleportConsumableDefinition definition)
        {
            if (!GameNetworkConfig.IsChannel100Listener(listenerGamePort))
                return true;
            if (definition == null || !definition.IsValid)
                return false;

            return definition.Kind == TeleportConsumableKind.FixedMap
                && definition.TargetTownId.HasValue
                && GameChannelSpawnPolicy.CanEnterTown(
                    listenerGamePort,
                    definition.TargetTownId.Value);
        }

        internal static bool CanUsePartyTeleport(int listenerGamePort)
            => !GameNetworkConfig.IsChannel100Listener(listenerGamePort);
    }
}
