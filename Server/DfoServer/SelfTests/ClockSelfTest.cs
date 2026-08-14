using DfoServer.Infrastructure;
using System;

namespace DfoServer.SelfTests
{
    // 时钟服务自测(合成时间注入, 零 sleep 全确定性):
    // 首查只定位不触发/跨分节拍/每日每周时刻/同分不重复/间隙塌缩/异常隔离/参数校验。
    public static class ClockSelfTest
    {
        private static int _pass;
        private static int _fail;

        // 北京墙钟 → UTC
        private static DateTime Bj(int y, int mo, int d, int h, int mi, int s = 0)
            => new DateTime(y, mo, d, h, mi, s).AddHours(-8);

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== CLOCK selftest ===");

            // 锚定日历事实(错了后面全错, 先炸出来)
            Check("2026-07-04 是周六", new DateTime(2026, 7, 4).DayOfWeek == DayOfWeek.Saturday);
            Check("2026-07-08 是周三", new DateTime(2026, 7, 8).DayOfWeek == DayOfWeek.Wednesday);

            var clock = new ClockService();
            int minuteFires = 0, dailyFires = 0, weeklyFires = 0;
            clock.RegisterMinuteTick("t-minute", _ => minuteFires++);
            clock.RegisterDailyMoment("t-daily-6", 6, 0, _ => dailyFires++);
            clock.RegisterWeeklyMoment("t-weekly-wed-20", DayOfWeek.Wednesday, 20, 0, _ => weeklyFires++);

            // ── 首查只定位, 不补启动前的时刻 ──
            clock.CheckOnce(Bj(2026, 7, 4, 5, 58, 0));
            Check("首查不触发任何回调", minuteFires == 0 && dailyFires == 0 && weeklyFires == 0);

            // ── 分钟节拍 ──
            clock.CheckOnce(Bj(2026, 7, 4, 5, 58, 3));
            Check("同一分钟内再查不触发", minuteFires == 0);
            clock.CheckOnce(Bj(2026, 7, 4, 5, 59, 1));
            Check("跨整分触发节拍", minuteFires == 1);
            Check("06:00前每日时刻未触发", dailyFires == 0);

            // ── 每日时刻 ──
            clock.CheckOnce(Bj(2026, 7, 4, 6, 0, 2));
            Check("跨过06:00触发每日时刻", dailyFires == 1);
            Check("跨分同时触发节拍", minuteFires == 2);
            clock.CheckOnce(Bj(2026, 7, 4, 6, 0, 7));
            Check("同一时刻不重复触发", dailyFires == 1);

            // ── 卡顿跳过多分钟: 节拍不补齐, 只触发一次 ──
            clock.CheckOnce(Bj(2026, 7, 4, 6, 3, 0));
            Check("跳过3分钟节拍只触发一次", minuteFires == 3);

            // ── 次日再次触发每日时刻 ──
            clock.CheckOnce(Bj(2026, 7, 5, 6, 0, 30));
            Check("次日06:00再次触发", dailyFires == 2);

            // ── 长间隙塌缩: 跨三天只触发一次 ──
            clock.CheckOnce(Bj(2026, 7, 8, 7, 0, 0));
            Check("三天间隙每日时刻只触发一次", dailyFires == 3);

            // ── 每周时刻(周三20:00) ──
            Check("周三20:00前未触发", weeklyFires == 0);
            clock.CheckOnce(Bj(2026, 7, 8, 19, 59, 0));
            Check("周三19:59未触发", weeklyFires == 0);
            clock.CheckOnce(Bj(2026, 7, 8, 20, 0, 4));
            Check("周三20:00触发每周时刻", weeklyFires == 1);
            clock.CheckOnce(Bj(2026, 7, 9, 20, 0, 4));
            Check("周四20:00不触发", weeklyFires == 1);
            Check("周四检查顺带跨过当日06:00", dailyFires == 4);
            clock.CheckOnce(Bj(2026, 7, 15, 20, 1, 0));
            Check("下周三再次触发", weeklyFires == 2);
            Check("六天间隙每日时刻仍只触发一次", dailyFires == 5);

            // ── 系统时钟回拨: 重新定位不触发, 已过时刻可再次到来 ──
            clock.CheckOnce(Bj(2026, 7, 15, 18, 0, 0));   // 从 20:01 拨回 18:00 (此前共跨9个整分)
            Check("回拨轮不触发任何回调", minuteFires == 9 && dailyFires == 5 && weeklyFires == 2);
            clock.CheckOnce(Bj(2026, 7, 15, 18, 1, 0));
            Check("回拨后分钟节拍恢复", minuteFires == 10);
            clock.CheckOnce(Bj(2026, 7, 15, 20, 0, 30));
            Check("回拨后同一周三20:00重新触发", weeklyFires == 3);

            // ── 异常隔离: 前一个回调抛异常不影响后一个 ──
            // 一次性 timer: 到期顺序、同名替换、惰性取消。
            var oneShotClock = new ClockService();
            var oneShotFires = 0;
            oneShotClock.ScheduleOneShot("once", Bj(2026, 7, 4, 10, 0, 10), _ => oneShotFires++);
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 0, 0));
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 0, 9));
            Check("one-shot到期前不触发", oneShotFires == 0);
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 0, 10));
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 0, 20));
            Check("one-shot只触发一次", oneShotFires == 1);

            var replacedFires = 0;
            oneShotClock.ScheduleOneShot("replace", Bj(2026, 7, 4, 10, 1, 0), _ => replacedFires += 10);
            oneShotClock.ScheduleOneShot("replace", Bj(2026, 7, 4, 10, 2, 0), _ => replacedFires++);
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 1, 30));
            Check("同名one-shot替换旧调度", replacedFires == 0);
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 2, 1));
            Check("同名one-shot只执行最新调度", replacedFires == 1);

            var cancelledFires = 0;
            oneShotClock.ScheduleOneShot("cancel", Bj(2026, 7, 4, 10, 3, 0), _ => cancelledFires++);
            Check("CancelOneShot取消存活调度返回true", oneShotClock.CancelOneShot("cancel"));
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 4, 0));
            Check("已取消one-shot不触发", cancelledFires == 0);

            var handleFires = 0;
            var oldHandle = oneShotClock.ScheduleOneShot("handle", Bj(2026, 7, 4, 10, 5, 0), _ => handleFires += 10);
            oneShotClock.ScheduleOneShot("handle", Bj(2026, 7, 4, 10, 6, 0), _ => handleFires++);
            Check("旧one-shot句柄不能取消替换后的新调度", !oldHandle.Cancel());
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 6, 1));
            Check("替换后的新调度不受旧句柄影响", handleFires == 1);

            var prefixFires = 0;
            oneShotClock.ScheduleOneShot("raid:1:ready", Bj(2026, 7, 4, 10, 7, 0), _ => prefixFires += 10);
            oneShotClock.ScheduleOneShot("raid:1:attack", Bj(2026, 7, 4, 10, 7, 0), _ => prefixFires += 10);
            oneShotClock.ScheduleOneShot("raid:2:ready", Bj(2026, 7, 4, 10, 7, 0), _ => prefixFires++);
            Check("前缀取消会移除匹配timer", oneShotClock.CancelOneShotsByPrefix("raid:1:") == 2);
            oneShotClock.CheckOnce(Bj(2026, 7, 4, 10, 7, 1));
            Check("前缀取消不影响其他团本timer", prefixFires == 1);

            var compactClock = new ClockService();
            var compactFires = 0;
            for (var i = 0; i < 1100; i++)
                compactClock.ScheduleOneShot("compact", Bj(2026, 7, 4, 11, 0, 0).AddSeconds(i), _ => compactFires++);
            var compactSnapshot = compactClock.GetDebugSnapshot();
            Check("大量同名替换会压缩惰性取消节点",
                compactSnapshot.OneShotTimers == 1
                && compactSnapshot.QueuedEntries < 128
                && compactSnapshot.LazyCancelledOneShots < 128);
            compactClock.CheckOnce(Bj(2026, 7, 4, 11, 20, 0));
            Check("压缩后仍只触发最新one-shot", compactFires == 1);

            var clock2 = new ClockService();
            var survived = 0;
            clock2.RegisterMinuteTick("t-throws", _ => throw new InvalidOperationException("boom"));
            clock2.RegisterMinuteTick("t-survives", _ => survived++);
            clock2.CheckOnce(Bj(2026, 7, 4, 10, 0, 0));
            clock2.CheckOnce(Bj(2026, 7, 4, 10, 1, 0));
            Check("异常回调不影响后续回调", survived == 1);

            // ── 参数校验: 宁可大声报错 ──
            var thrown = false;
            try { clock2.RegisterDailyMoment("t-bad", 24, 0, _ => { }); }
            catch (ArgumentOutOfRangeException) { thrown = true; }
            Check("非法小时注册抛异常", thrown);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
