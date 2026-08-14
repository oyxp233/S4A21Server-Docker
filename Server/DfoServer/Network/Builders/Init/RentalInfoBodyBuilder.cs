using System;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class RentalInfoBodyBuilder : IInitPacketBuilder
    {
        private readonly IRentalTimeProvider _rentalTimeProvider;

        public RentalInfoBodyBuilder()
            : this(SystemRentalTimeProvider.Instance)
        {
        }

        public RentalInfoBodyBuilder(IRentalTimeProvider rentalTimeProvider)
        {
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
        }

        public ushort NotiType => 0x0357;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            body = BuildWireBody(init.LuckyStar, init.RentalInfo, _rentalTimeProvider.UtcNowUnixSeconds());
            return true;
        }

        public static byte[] BuildWireBody(ushort luckyStar, RentalInfoSnapshot rental)
            => BuildWireBody(luckyStar, rental, SystemRentalTimeProvider.Instance.UtcNowUnixSeconds());

        internal static byte[] BuildWireBody(ushort luckyStar, RentalInfoSnapshot rental, uint nowUnixSeconds)
        {
            var info = rental ?? new RentalInfoSnapshot();
            var activeItems = new System.Collections.Generic.List<RentalItemSnapshot>();
            for (var i = 0; i < info.Items.Count; i++)
            {
                var item = info.Items[i];
                if (item.ItemId == 0 || item.ExpireTime <= nowUnixSeconds)
                    continue;

                activeItems.Add(item);
            }

            var itemCount = activeItems.Count;
            var body = new byte[8 + itemCount * 8];
            Buffer.BlockCopy(BitConverter.GetBytes((uint)luckyStar), 0, body, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)itemCount), 0, body, 4, 4);
            for (var i = 0; i < itemCount; i++)
            {
                var off = 8 + i * 8;
                var item = activeItems[i];
                // 客户端真实格式：幸运星 + 条目数 + (背包模板ID + 绝对到期秒)*，不包含租赁ID/商店条目ID。
                var inventoryTemplateId = item.InventoryTemplateId != 0 ? item.InventoryTemplateId : item.ItemId;
                Buffer.BlockCopy(BitConverter.GetBytes(inventoryTemplateId), 0, body, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(item.ExpireTime), 0, body, off + 4, 4);
            }

            return body;
        }
    }
}
