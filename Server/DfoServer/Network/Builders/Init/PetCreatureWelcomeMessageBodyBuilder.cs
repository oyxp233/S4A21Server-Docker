using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    public sealed class PetCreatureWelcomeMessageBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0077;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = null;
            if (occurrenceIndex != 0)
                return false;

            var character = snapshot?.CharacterRecord;
            var characterId = character?.CharacterId ?? 0;
            var itemTemplateId = ResolveEquippedCreatureItemId(snapshot);
            if (characterId <= 0 || itemTemplateId <= 0)
                return false;

            return PetCreatureScript.TryBuildWelcomeBody(itemTemplateId, characterId, out body);
        }

        private static int ResolveEquippedCreatureItemId(SelectCharacterDataSnapshot snapshot)
        {
            var characterId = snapshot?.CharacterRecord?.CharacterId ?? 0;
            if (characterId > 0 && InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                {
                    var creature = lease.Inventory.GetItem(
                        InventoryListType.Equipment,
                        PetInventoryLayout.CreatureEquipSlot);
                    if (creature != null && creature.ItemKind == ItemCore.KindCreature)
                        return creature.ItemId;
                }
            }

            var tailItemId = snapshot?.CharacterRecord?.Subtype0Tail?.EquippedCreatureItemId ?? 0;
            if (tailItemId > 0)
                return unchecked((int)tailItemId);

            return 0;
        }
    }
}
