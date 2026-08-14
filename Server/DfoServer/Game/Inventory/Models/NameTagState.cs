using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class NameTagState
    {
        public int ItemId { get; private set; }

        public int ExpireTime { get; private set; }

        public bool IsActive()
        {
            return ItemId > 0
                && (ExpireTime <= 0 || ExpireTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public int GetRemainSeconds()
        {
            if (ItemId <= 0)
                return 0;
            if (ExpireTime <= 0)
                return 0;

            var remain = ExpireTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return remain <= 0 ? 0 : (int)Math.Min(int.MaxValue, remain);
        }

        public void Set(int itemId, int expireTime)
        {
            ItemId = Math.Max(0, itemId);
            ExpireTime = Math.Max(0, expireTime);
        }

        public void Clear()
        {
            ItemId = 0;
            ExpireTime = 0;
        }

        public NameTagState Copy()
        {
            var copy = new NameTagState();
            copy.Set(ItemId, ExpireTime);
            return copy;
        }
    }
}
