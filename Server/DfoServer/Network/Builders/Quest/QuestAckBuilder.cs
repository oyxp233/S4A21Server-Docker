using DfoServer.Game.Quests;

namespace DfoServer.Network.Builders
{
    // 任务四个命令应答包的唯一序列化点 -- 应答字节格式只出现在这里,
    // 由 QuestAckFormatSelfTest 逐字节冻结。业务侧(QuestService/QuestManager)
    // 只与 QuestResults 里的结构化对象打交道。
    public static class QuestAckBuilder
    {
        public static byte[] BuildAccept(QuestAcceptResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            w.WriteUInt32(r.InitTrigger);
            w.WriteByte((byte)r.EventItems.Count);
            foreach (var item in r.EventItems)
            {
                w.WriteUInt16(item.SlotIndex);
                w.WriteUInt32((uint)item.ItemId);
                w.WriteUInt32((uint)item.Count);
            }
            return w.ToArray();
        }

        public static byte[] BuildGiveup(QuestGiveupResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            return w.ToArray();
        }

        public static byte[] BuildSetTrigger(QuestSetTriggerResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            w.WriteUInt32(r.TriggerValue);
            return w.ToArray();
        }

        public static byte[] BuildFinish(QuestFinishResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            w.WriteByte(0x00); // completionType=0 (type 0/25)
            w.WriteUInt32(r.Exp);
            w.WriteUInt32(r.Gold);

            w.WriteByte((byte)r.ConsumedEntries.Count);
            foreach (var ce in r.ConsumedEntries)
            {
                w.WriteByte(ce.UpdateType);
                w.WriteUInt16(ce.SlotIndex);
                w.WriteUInt32(ce.ConsumedCount);
            }

            w.WriteByte((byte)r.ChainType);
            if (r.ChainType == 0)
            {
                w.WriteByte((byte)r.InsertedEntries.Count);
                foreach (var ie in r.InsertedEntries)
                {
                    w.WriteUInt16(ie.SlotIndex);
                    w.WriteUInt32((uint)ie.ItemId);
                    w.WriteUInt32(ie.CountOrSeed);
                    w.WriteByte(0);   // upgradeLevel
                    w.WriteUInt16(ie.EquipDurability);
                    w.WriteUInt32(0); // reserved
                    w.WriteByte(0);   // extraFlags
                }
            }
            else if (r.ChainType == 1 || r.ChainType == 2
                || r.ChainType == 20
                || r.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
            {
                w.WriteByte((byte)r.GrowNumber);
                w.WriteByte(0); // npcCount layer 1
                w.WriteByte(0); // npcCount layer 2
            }
            return w.ToArray();
        }

        private static byte[] BuildFail(byte errorCode)
        {
            return new byte[] { 0x00, errorCode };
        }
    }
}
