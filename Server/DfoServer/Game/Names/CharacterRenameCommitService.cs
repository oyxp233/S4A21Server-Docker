using System;
using System.Linq;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Names
{
    internal static class CharacterRenameCommitService
    {
        internal static bool TryCommit(InventoryLease lease,int account,byte[] oldName,byte[] newName,long now)
            =>OnlineInventoryMutationCommitCoordinator.TryCommit(lease,"character-rename",(c,t)=>
            {
                if (lease.AccountId != account || oldName == null || newName == null
                    || oldName.AsSpan().SequenceEqual(newName)
                    || !NameInputValidator.TryValidateRawName(newName, 2, 18, out _, out _)) return false;
                var source=lease.Inventory.GetItems(InventoryListType.Main).FirstOrDefault(p=>CharacterRenameRepository.IsUsableCard(p.Value,now)
                    && InventoryDeleteService.CanUseStackableForClient(lease.Inventory,InventoryListType.Main,p.Key,CharacterRenameRepository.CardId,out _));
                if(source.Value==null)return false;
                var plan=InventoryItemLifecycleService.PrepareUse(lease.Inventory,InventoryListType.Main,source.Key,CharacterRenameRepository.CardId,now);
                if(!plan.Success || !CharacterRenameRepository.TryChange(c,t,account,lease.CharacterId,oldName,newName,now)
                    || !InventoryDeleteService.TryUseStackableForClient(lease.Inventory,InventoryListType.Main,source.Key,CharacterRenameRepository.CardId,out _))return false;
                InventoryItemLifecycleService.ApplyUseSuccess(lease.Inventory,plan);return true;
            });
    }
}
