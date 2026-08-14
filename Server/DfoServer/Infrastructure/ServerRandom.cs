using System;

namespace DfoServer.Infrastructure
{
    // 服务端裁决随机的唯一入口(线程安全)。
    //
    // 全仓随机数分两类, 不可混用:
    // - 客户端同步随机: 必须走房间 LCG(DnfLcg, 种子随开图包下发, 与客户端逐步对齐),
    //   掉落序列等凡是客户端要按同一种子重放的都属此类, 换随机源会破坏对齐;
    // - 服务端裁决随机: 结果只体现在服务端后续下发的数据里(选迷宫/冠军怪抽取/
    //   神秘商店 NPC/深渊难度权重等), 用本类。
    //
    // System.Random 非线程安全, 并发调用会损坏内部状态(之后恒返回 0);
    // 副本域存在脱离会话串行的定时器线程, 因此统一加锁。
    public static class ServerRandom
    {
        private static readonly Random _rng = new Random();
        private static readonly object _lock = new object();

        public static int Next()
        {
            lock (_lock) return _rng.Next();
        }

        public static int Next(int maxValue)
        {
            lock (_lock) return _rng.Next(maxValue);
        }

        public static int Next(int minValue, int maxValue)
        {
            lock (_lock) return _rng.Next(minValue, maxValue);
        }
    }
}
