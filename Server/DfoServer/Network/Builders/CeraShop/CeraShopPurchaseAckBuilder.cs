using DfoServer.Game.Inventory;
using DfoServer.Network.Parsers.CeraShop;

namespace DfoServer.Network.Builders.CeraShop
{
    public static class CeraShopPurchaseAckBuilder
    {
        // 客户端 sub_CD9490 以 body[1] 选择商城购买失败提示。
        public const byte ErrorCodeInventoryFull = 4;
        public const byte ErrorCodeInsufficientCera = 11;

        // 商城购买失败码 body[1](客户端 sub_CD9490 提示表, 实测穷举结果, 2026-09):
        //   4 = 物品栏空间不足           7 = 在决斗频道中无法购买<商品名>(无关文案, 勿用)
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
        // 金库/仓库满档(NoEffect)使用 113 "限制购买的物品"。
        public const byte ErrorCodeCannotBuy = 113;

        public static byte[] BuildSuccess(CeraShopPurchaseRequest request, InventoryMutationResult result)
        {
            int commodityNo = (request != null && request.CommodityNos.Count > 0) ? request.CommodityNos[0] : 0;
            return BuildSuccess(commodityNo, result);
        }

        public static byte[] BuildSuccess(int commodityNo, InventoryMutationResult result)
        {
            // 购买结果弹窗(CMD 0x40 成功)body 格式 (逆向 sub_CD9490 成功分支得到):
            //   [0]      result flag (1=成功; 客户端取为 a2, 非0走成功分支)
            //   客户端从 body[1] 起按流读取:
            //   [1]      U8   var_805  标志
            //   [2..5]   U32  category 类别索引; sub_72F060: >=0 只查该类(越界会崩), <0 遍历全部 24 类。
            //                  填 -1 => 全类别搜索 commodityNo, 任何商品都能查到, 且不越界。
            //   [6..9]   U32  commodityNo  所购商品号(主商品, sub_74F670 据此查表显示物品图标/名)
            //   [10..13] U32  var_818
            //   [14..17] U32  var_81C
            //   [18..21] U32  var_824  (=0 时 sub_74F670 才显示主商品)
            //   [22..23] U16  count    "额外追加项"数量; 单个普通商品填 0(主商品已由 sub_74F670 显示)
            //   [24..]   每项 U32 + U32 (额外项, 单商品不需要)
            // 注: 之前填 count=1 + var_828=1, 客户端把 1 当 itemId 多显示了"复活币", 故置 0。
            // 自动拆出的实际物品由 CeraShopHandler 汇总到 NOTI 0x000E；不要放入这里，
            // 否则客户端会走商城购买职业校验分支，弹出"购买其他职业的物品"警告。
            var writer = new GamePacketWriter();

            writer.WriteByte(1);            // [0]      result flag = 成功
            writer.WriteByte(0);            // [1]      var_805
            writer.WriteInt32(-1);          // [2..5]   category = -1 (全类别搜索, 兼容所有商品)
            writer.WriteInt32(commodityNo); // [6..9]   commodityNo (主商品)
            writer.WriteInt32(0);           // [10..13] var_818
            writer.WriteInt32(0);           // [14..17] var_81C
            writer.WriteInt32(0);           // [18..21] var_824 = 0
            writer.WriteInt16(0);           // [22..23] count = 0 (无额外项)

            return writer.ToArray();
        }

        public static byte[] BuildError(CeraShopPurchaseRequest request = null)
        {
            return BuildError(ErrorCodeInventoryFull, request);
        }

        public static byte[] BuildError(byte errorCode, CeraShopPurchaseRequest request = null)
        {
            // 失败回包: 客户端失败分支(sub_CD9490, a2=0)在 default 错误码下会继续从流里读
            //   var_805(U8) + 5个U32 (共21字节), 并在 CD9ADF 用 body[2..5]作category、body[6..9]作
            //   commodityNo 调 sub_72F060。若 body 太短(原来只有2字节), 这些字段读到下一个包的垃圾,
            //   被当索引 -> 越界崩。故补足 22 字节, 并令 body[6..9]=-1 (a3=-1 时 sub_72F060 立即返回, 不崩)。
            var writer = new GamePacketWriter();
            writer.WriteByte(0);   // [0]      result = 失败
            writer.WriteByte(errorCode); // [1]  var_805 (客户端错误码)
            writer.WriteInt32(-1); // [2..5]   body[2..5] (CD9ADF 作 category, -1=全搜索安全)
            writer.WriteInt32(-1); // [6..9]   body[6..9] (CD9ADF 作 commodityNo; =-1 时 sub_72F060 立即返回)
            writer.WriteInt32(0);  // [10..13]
            writer.WriteInt32(0);  // [14..17]
            writer.WriteInt32(0);  // [18..21]
            return writer.ToArray();
        }
    }
}
