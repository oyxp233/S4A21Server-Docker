using DfoServer.Game.Inventory;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class BuyItemAckBuilder
    {
        // NPC 商店购买失败回包 body[1] 错误码。
        // 本弹窗(0x0015 失败分支)自有提示码表, 与商城 sub_CD9490 的表互相独立。
        // 2026-10-11 全字节域实测穷举结果(NPC 商店购买弹窗):
        //   4 = 物品栏空间不足          10 = 金币不足
        //   20 = 无法购买的物品         133 = 超过购买限制
        //   206 = 被禁闭在赛丽亚旅馆    214 = 物品已锁定
        //   1/12/13/17/19/25/26/113/114 实测静默(不弹窗)。
        //   注: 商城表的 113=限制购买的物品、26=超出当月点券购买上限 在本弹窗均不接线, 勿用;
        //       11(点券不足)是商城表码, 在本弹窗未实测、有静默不弹窗风险, 已弃用; 点券不足改走已实测的 20;
        //       105/131/134/137/210 有跳安全页/二级密码/闪退风险, 勿用。
        // 商城购买失败码 body[1] (客户端 sub_CD9490 提示表, 实测穷举结果, 2026-09):
        //   4 = 物品栏空间不足           7 = 在决斗频道中无法购买(无关文案, 勿用)
        //   10 = 金币不足                11 = 点券不足
        //   12 = 胜点不足                13 = LV不足
        //   14 = 静默处理(不弹窗)        17 = 不存在的物品
        //   19 = 使用期限设置错误         20 = 无法购买的物品
        //   21 = 错误的商品编号           23 = 物品功能设置错误
        //   25 = 付费服务器错误           26 = 超出当月点券购买上限
        //   60 = 个人商店开设中无法进入商城
        //   77 = 无法与黑名单角色聊天     105 = 安全验证后才可以交易
        //   108 = 装扮兑换券不足          109 = 无法作为礼物赠送
        //   113 = 限制购买的物品         118 = 静默处理(不弹窗)
        //   131 = 触发安全页跳转(闪退, 勿用)
        //   134 = 需二级密码认证          137 = 盗号保护交易限制
        //   162 = 无法使用优惠券          163 = 每日10万点券赠送上限
        // 禁用码(有副作用或静默, 不得使用): 7、14、60、77、105、118、131、162、163。
        public const byte ErrorCodeInventoryFull = 4;
        public const byte ErrorCodeInsufficientGold = 10;
        public const byte ErrorCodeCannotBuy = 20;
        public const byte ErrorCodePurchaseLimited = 133;

        // 购买失败原因 → body[1] 错误码。材料不足暂无专属码，用金币不足(10)保守提示;
        // 点券不足(11)是商城表码, 在本弹窗未实测、有静默不弹窗风险, 已弃用;
        // 点券不足正常已被客户端本地预检拦截, 服务端命中该分支属异常状态, 用通用弹窗码 20 即可。
        internal static byte ResolveErrorCode(ShopBuyFailureReason reason)
        {
            switch (reason)
            {
                case ShopBuyFailureReason.PurchaseLimitExceeded:
                    return ErrorCodePurchaseLimited;
                case ShopBuyFailureReason.InsufficientGold:
                case ShopBuyFailureReason.InsufficientMaterial:
                    return ErrorCodeInsufficientGold;
                case ShopBuyFailureReason.InventoryFull:
                    return ErrorCodeInventoryFull;
                case ShopBuyFailureReason.InsufficientCera:
                case ShopBuyFailureReason.InvalidRequest:
                default:
                    return ErrorCodeCannotBuy;
            }
        }

        public static byte[] Build(InventoryMutationResult result, List<PurchaseCountUpdate> purchaseCountUpdates = null)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(result.UpdatedGold);
            writer.WriteInt32(result.UpdatedSp);
            writer.WriteInt32(0);
            writer.WriteInt32(result.UpdatedCoin);

            if (result.CoreSnapshot != null && result.SlotIndex >= 0)
            {
                ItemListProtocolWriter.WriteCommonEntry84(
                    writer,
                    result.SlotIndex,
                    result.CoreSnapshot,
                    result.RequestedCount);
            }
            else
            {
                WriteLegacyItemSummary(writer, result);
            }

            var count = purchaseCountUpdates != null ? purchaseCountUpdates.Count : 0;
            writer.WriteByte((byte)count);
            if (purchaseCountUpdates != null)
            {
                foreach (var update in purchaseCountUpdates)
                {
                    writer.WriteInt32(update.ItemTemplateId);
                    writer.WriteInt32(update.RequestedCount);
                }
            }

            return writer.ToArray();
        }

        private static void WriteLegacyItemSummary(
            GamePacketWriter writer,
            InventoryMutationResult result)
        {
            writer.WriteInt16(result.SlotIndex);
            writer.WriteInt32(result.ItemTemplateId);
            writer.WriteInt32(result.InstanceValue);
            writer.WriteUInt16(result.Durability);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writer.WriteInt32(result.ExpireTime);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }

    public sealed class PurchaseCountUpdate
    {
        public int ItemTemplateId { get; set; }

        public int RequestedCount { get; set; }
    }
}
