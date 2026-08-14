using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Infrastructure
{
    /// <summary>
    /// 进程内时间队列, 用于在线通知和短生命周期的运行时控制。
    /// </summary>
    /// <remarks>
    /// 设计上对齐旧服的堆式 TimerQueue: 回调按 UTC 到期时间排序, 已取消/被替换的节点采用惰性清理,
    /// 每分钟/每日/每周这类周期任务在卡顿后只触发一次, 不补放每个错过的时间点。
    ///
    /// 适合接入团本 ready 倒计时、攻坚阶段计时、在线宠物运行时检查、UI 刷新通知等进程内控制流。
    /// 不要把它当成奖励、次数限制、每日重置、团本持久进度等跨重启状态的唯一真相来源。
    /// 这类功能必须先落库保存权威状态, 登录或启动时再按持久化状态重建仍需要的 timer。
    ///
    /// 一次性 timer 命名约定:
    ///   domain:key:stage
    /// 示例:
    ///   raid:1002:ready
    ///   raid:1002:attack
    ///   pet-death:{sessionId}
    /// 整个聚合对象销毁时, 用 CancelOneShotsByPrefix("raid:1002:") 批量清理。
    /// </remarks>
    public sealed class ClockService
    {
        public static readonly ClockService Instance = new ClockService();

        private const int TimeZoneOffsetHours = 8;
        private const int MaxTimerDueMs = int.MaxValue - 1;
        private const int QueueCompactionStaleThreshold = 1024;

        private enum ScheduledKind
        {
            MinuteTick,
            Moment,
            OneShot,
        }

        private sealed class MomentEntry
        {
            public string Name;
            public int Hour;
            public int Minute;
            public DayOfWeek? Day;
            public DateTime NextDueUtc;
            public Action<DateTime> Callback;
        }

        private sealed class ScheduledEntry
        {
            public string Name;
            public long Id;
            public ScheduledKind Kind;
            public DateTime DueUtc;
            public Action<DateTime> Callback;
            public MomentEntry Moment;
            public int Generation;
            public bool Cancelled;
        }

        private readonly object _sync = new object();
        private readonly TimeQueue<ScheduledEntry> _queue = new TimeQueue<ScheduledEntry>();
        private readonly List<KeyValuePair<string, Action<DateTime>>> _minuteTicks
            = new List<KeyValuePair<string, Action<DateTime>>>();
        private readonly List<MomentEntry> _moments = new List<MomentEntry>();
        private readonly Dictionary<string, ScheduledEntry> _oneShots
            = new Dictionary<string, ScheduledEntry>(StringComparer.Ordinal);

        private DateTime _lastCheckedUtc = DateTime.MinValue;
        private bool _anchored;
        private int _generation;
        private long _nextEntryId;
        private int _staleOneShotNodes;
        private Timer _timer;
        private int _checking;

        public sealed class ClockTimerHandle
        {
            private readonly ClockService _owner;
            private readonly long _id;

            internal ClockTimerHandle(ClockService owner, string name, long id, DateTime dueUtc)
            {
                _owner = owner;
                Name = name;
                _id = id;
                DueUtc = dueUtc;
            }

            public string Name { get; }
            public DateTime DueUtc { get; }

            /// <summary>
            /// 取消当前句柄对应的那一次调度。若该 timer 已触发、已取消, 或已被同名新 timer 替换, 返回 false。
            /// </summary>
            public bool Cancel()
                => _owner.CancelOneShot(Name, _id);
        }

        internal readonly struct ClockDebugSnapshot
        {
            public ClockDebugSnapshot(
                int queuedEntries,
                int oneShotTimers,
                int lazyCancelledOneShots,
                int minuteTickCallbacks,
                int momentCallbacks,
                bool anchored,
                DateTime lastCheckedUtc)
            {
                QueuedEntries = queuedEntries;
                OneShotTimers = oneShotTimers;
                LazyCancelledOneShots = lazyCancelledOneShots;
                MinuteTickCallbacks = minuteTickCallbacks;
                MomentCallbacks = momentCallbacks;
                Anchored = anchored;
                LastCheckedUtc = lastCheckedUtc;
            }

            public int QueuedEntries { get; }
            public int OneShotTimers { get; }
            public int LazyCancelledOneShots { get; }
            public int MinuteTickCallbacks { get; }
            public int MomentCallbacks { get; }
            public bool Anchored { get; }
            public DateTime LastCheckedUtc { get; }
        }

        /// <summary>
        /// 注册每分钟节拍回调。墙钟跨过 UTC 整分钟时触发一次; 长时间卡顿只合并成一次回调, 不补齐跳过的分钟。
        /// </summary>
        public void RegisterMinuteTick(string name, Action<DateTime> callback)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty", nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            lock (_sync)
            {
                var wasEmpty = _minuteTicks.Count == 0;
                _minuteTicks.Add(new KeyValuePair<string, Action<DateTime>>(name, callback));

                if (_anchored && wasEmpty)
                {
                    EnqueueMinuteTickLocked(_lastCheckedUtc);
                    ArmTimerLocked(DateTime.UtcNow);
                }
            }
        }

        /// <summary>
        /// 注册每日北京时间 HH:mm 回调。首次检查只定位下一次触发点, 不补放服务启动前错过的时刻。
        /// </summary>
        public void RegisterDailyMoment(string name, int hour, int minute, Action<DateTime> callback)
            => RegisterMoment(name, null, hour, minute, callback);

        /// <summary>
        /// 注册每周指定星期的北京时间 HH:mm 回调。首次检查只定位下一次触发点, 不补放服务启动前错过的时刻。
        /// </summary>
        public void RegisterWeeklyMoment(string name, DayOfWeek day, int hour, int minute, Action<DateTime> callback)
            => RegisterMoment(name, day, hour, minute, callback);

        /// <summary>
        /// 按相对延迟注册一次性回调。小于等于 0 的延迟会在调度器下一次检查时尽快触发。
        /// 同名调度会替换旧调度。
        /// </summary>
        public ClockTimerHandle ScheduleOneShotAfter(string name, TimeSpan delay, Action<DateTime> callback)
            => ScheduleOneShot(name, DateTime.UtcNow.Add(NormalizeDelay(delay)), callback);

        /// <summary>
        /// ScheduleOneShotAfter 的异步版本。异步回调采用 fire-and-forget, 异常由 ClockService 捕获并写日志。
        /// </summary>
        public ClockTimerHandle ScheduleOneShotAfterAsync(string name, TimeSpan delay, Func<DateTime, Task> callback)
            => ScheduleOneShotAsync(name, DateTime.UtcNow.Add(NormalizeDelay(delay)), callback);

        /// <summary>
        /// 按绝对时间注册异步一次性回调。异步回调采用 fire-and-forget, 异常由 ClockService 捕获并写日志。
        /// </summary>
        public ClockTimerHandle ScheduleOneShotAsync(string name, DateTime dueUtc, Func<DateTime, Task> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            return ScheduleOneShot(name, dueUtc, utcNow => _ = RunAsyncCallback(name, callback, utcNow));
        }

        /// <summary>
        /// 按绝对 UTC 时间注册一次性回调。Local DateTime 会转换成 UTC, Unspecified DateTime 按 UTC 处理。
        /// 同名调度会替换旧调度。若后续必须取消这一"具体一次"调度, 请保存返回的句柄。
        /// </summary>
        public ClockTimerHandle ScheduleOneShot(string name, DateTime dueUtc, Action<DateTime> callback)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty", nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            dueUtc = NormalizeUtc(dueUtc);

            lock (_sync)
            {
                if (_oneShots.TryGetValue(name, out var previous))
                {
                    previous.Cancelled = true;
                    NoteStaleOneShotQueuedLocked();
                }

                var entry = new ScheduledEntry
                {
                    Name = name,
                    Id = NextEntryIdLocked(),
                    Kind = ScheduledKind.OneShot,
                    DueUtc = dueUtc,
                    Callback = callback,
                };
                _oneShots[name] = entry;
                _queue.Enqueue(entry, dueUtc);
                CompactQueueIfNeededLocked();
                ArmTimerLocked(DateTime.UtcNow);
                return new ClockTimerHandle(this, name, entry.Id, dueUtc);
            }
        }

        /// <summary>
        /// 取消指定名称的最新一次性调度。没有存活调度时返回 false。
        /// 若旧调用方不能误取消同名新调度, 优先使用 ClockTimerHandle.Cancel。
        /// </summary>
        public bool CancelOneShot(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            lock (_sync)
            {
                if (!_oneShots.TryGetValue(name, out var entry))
                    return false;

                entry.Cancelled = true;
                NoteStaleOneShotQueuedLocked();
                _oneShots.Remove(name);
                CompactQueueIfNeededLocked();
                ArmTimerLocked(DateTime.UtcNow);
                return true;
            }
        }

        /// <summary>
        /// 批量取消所有名称以前缀开头的一次性调度, 返回取消数量。
        /// 用于聚合对象销毁, 例如取消 raid:{raidKey}:* 下的全部团本 timer。
        /// </summary>
        public int CancelOneShotsByPrefix(string namePrefix)
        {
            if (string.IsNullOrEmpty(namePrefix))
                return 0;

            lock (_sync)
            {
                var names = new List<string>();
                foreach (var pair in _oneShots)
                    if (pair.Key.StartsWith(namePrefix, StringComparison.Ordinal))
                        names.Add(pair.Key);

                foreach (var name in names)
                {
                    _oneShots[name].Cancelled = true;
                    NoteStaleOneShotQueuedLocked();
                    _oneShots.Remove(name);
                }

                if (names.Count > 0)
                {
                    CompactQueueIfNeededLocked();
                    ArmTimerLocked(DateTime.UtcNow);
                }
                return names.Count;
            }
        }

        private bool CancelOneShot(string name, long id)
        {
            lock (_sync)
            {
                if (!_oneShots.TryGetValue(name, out var entry) || entry.Id != id)
                    return false;

                entry.Cancelled = true;
                NoteStaleOneShotQueuedLocked();
                _oneShots.Remove(name);
                CompactQueueIfNeededLocked();
                ArmTimerLocked(DateTime.UtcNow);
                return true;
            }
        }

        internal ClockDebugSnapshot GetDebugSnapshot()
        {
            lock (_sync)
            {
                return new ClockDebugSnapshot(
                    _queue.Count,
                    _oneShots.Count,
                    _staleOneShotNodes,
                    _minuteTicks.Count,
                    _moments.Count,
                    _anchored,
                    _lastCheckedUtc);
            }
        }

        private void RegisterMoment(string name, DayOfWeek? day, int hour, int minute, Action<DateTime> callback)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty", nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (hour < 0 || hour > 23) throw new ArgumentOutOfRangeException(nameof(hour), hour, "hour must be 0-23");
            if (minute < 0 || minute > 59) throw new ArgumentOutOfRangeException(nameof(minute), minute, "minute must be 0-59");

            lock (_sync)
            {
                var moment = new MomentEntry
                {
                    Name = name,
                    Hour = hour,
                    Minute = minute,
                    Day = day,
                    NextDueUtc = DateTime.MinValue,
                    Callback = callback,
                };
                _moments.Add(moment);

                if (_anchored)
                {
                    EnqueueMomentLocked(moment, _lastCheckedUtc);
                    ArmTimerLocked(DateTime.UtcNow);
                }
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_timer != null)
                    return;

                _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                EnsureAnchoredLocked(DateTime.UtcNow);
                ArmTimerLocked(DateTime.UtcNow);
            }
        }

        private void OnTimer(object state)
        {
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
                return;

            try
            {
                CheckOnce(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Clock] check error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
                RefreshTimer();
            }
        }

        internal void CheckOnce(DateTime utcNow)
        {
            utcNow = NormalizeUtc(utcNow);
            List<KeyValuePair<string, Action<DateTime>>> dueCallbacks = null;

            lock (_sync)
            {
                if (!_anchored)
                {
                    EnsureAnchoredLocked(utcNow);
                }
                else if (_lastCheckedUtc != DateTime.MinValue && utcNow < _lastCheckedUtc)
                {
                    FileLogger.Log($"[Clock] wall clock moved backwards ({_lastCheckedUtc:HH:mm:ss} -> {utcNow:HH:mm:ss} UTC), re-anchoring");
                    ReanchorRecurringLocked(utcNow);
                    _lastCheckedUtc = utcNow;
                    ArmTimerLocked(utcNow);
                    return;
                }

                _lastCheckedUtc = utcNow;

                while (_queue.TryPeek(out var entry, out var dueUtc))
                {
                    if (entry.Cancelled || IsStaleRecurringEntry(entry))
                    {
                        _queue.Dequeue();
                        NoteStaleEntryDequeuedLocked(entry);
                        continue;
                    }

                    if (dueUtc > utcNow)
                        break;

                    _queue.Dequeue();
                    CollectDueEntryLocked(entry, utcNow, ref dueCallbacks);
                }

                ArmTimerLocked(utcNow);
            }

            if (dueCallbacks != null)
                foreach (var callback in dueCallbacks)
                    Invoke(callback.Key, callback.Value, utcNow);

            RefreshTimer();
        }

        private void CollectDueEntryLocked(
            ScheduledEntry entry,
            DateTime utcNow,
            ref List<KeyValuePair<string, Action<DateTime>>> dueCallbacks)
        {
            switch (entry.Kind)
            {
                case ScheduledKind.MinuteTick:
                    if (_minuteTicks.Count > 0)
                    {
                        if (dueCallbacks == null)
                            dueCallbacks = new List<KeyValuePair<string, Action<DateTime>>>();
                        dueCallbacks.AddRange(_minuteTicks);
                        EnqueueMinuteTickLocked(utcNow);
                    }
                    break;

                case ScheduledKind.Moment:
                    if (entry.Moment == null)
                        break;
                    if (dueCallbacks == null)
                        dueCallbacks = new List<KeyValuePair<string, Action<DateTime>>>();
                    dueCallbacks.Add(new KeyValuePair<string, Action<DateTime>>(
                        entry.Moment.Name,
                        entry.Moment.Callback));
                    EnqueueMomentLocked(entry.Moment, utcNow);
                    break;

                case ScheduledKind.OneShot:
                    if (_oneShots.TryGetValue(entry.Name, out var current)
                        && ReferenceEquals(current, entry))
                    {
                        _oneShots.Remove(entry.Name);
                        if (dueCallbacks == null)
                            dueCallbacks = new List<KeyValuePair<string, Action<DateTime>>>();
                        dueCallbacks.Add(new KeyValuePair<string, Action<DateTime>>(
                            entry.Name,
                            entry.Callback));
                    }
                    break;
            }
        }

        private void EnsureAnchoredLocked(DateTime utcNow)
        {
            _anchored = true;
            ReanchorRecurringLocked(utcNow);
            _lastCheckedUtc = utcNow;
        }

        private void ReanchorRecurringLocked(DateTime utcNow)
        {
            unchecked
            {
                _generation++;
                if (_generation == 0)
                    _generation = 1;
            }

            if (_minuteTicks.Count > 0)
                EnqueueMinuteTickLocked(utcNow);

            foreach (var moment in _moments)
                EnqueueMomentLocked(moment, utcNow);
        }

        private void EnqueueMinuteTickLocked(DateTime utcNow)
        {
            var dueUtc = NextMinuteBoundaryUtc(utcNow);
            _queue.Enqueue(new ScheduledEntry
            {
                Name = "minute-tick",
                Kind = ScheduledKind.MinuteTick,
                DueUtc = dueUtc,
                Generation = _generation,
            }, dueUtc);
        }

        private void EnqueueMomentLocked(MomentEntry moment, DateTime utcNow)
        {
            var dueUtc = ComputeNextDueUtc(moment, utcNow);
            moment.NextDueUtc = dueUtc;
            _queue.Enqueue(new ScheduledEntry
            {
                Name = moment.Name,
                Kind = ScheduledKind.Moment,
                DueUtc = dueUtc,
                Moment = moment,
                Generation = _generation,
            }, dueUtc);
        }

        private bool IsStaleRecurringEntry(ScheduledEntry entry)
            => entry.Kind != ScheduledKind.OneShot && entry.Generation != _generation;

        private long NextEntryIdLocked()
        {
            unchecked
            {
                _nextEntryId++;
                if (_nextEntryId == 0)
                    _nextEntryId = 1;
            }

            return _nextEntryId;
        }

        private void RefreshTimer()
        {
            lock (_sync)
                ArmTimerLocked(DateTime.UtcNow);
        }

        private void ArmTimerLocked(DateTime utcNow)
        {
            if (_timer == null)
                return;

            while (_queue.TryPeek(out var entry, out _)
                   && (entry.Cancelled || IsStaleRecurringEntry(entry)))
            {
                _queue.Dequeue();
                NoteStaleEntryDequeuedLocked(entry);
            }

            if (!_queue.TryPeek(out _, out var dueUtc))
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            var delayMs = (dueUtc - utcNow).TotalMilliseconds;
            var dueTime = delayMs <= 0
                ? 0
                : delayMs >= MaxTimerDueMs
                    ? MaxTimerDueMs
                    : (int)Math.Ceiling(delayMs);
            _timer.Change(dueTime, Timeout.Infinite);
        }

        private void CompactQueueIfNeededLocked()
        {
            if (_staleOneShotNodes < QueueCompactionStaleThreshold)
                return;

            if (_staleOneShotNodes <= _oneShots.Count)
                return;

            _queue.Compact(IsQueueEntryLiveLocked);
            _staleOneShotNodes = 0;
        }

        private bool IsQueueEntryLiveLocked(ScheduledEntry entry)
        {
            if (entry == null || entry.Cancelled || IsStaleRecurringEntry(entry))
                return false;

            if (entry.Kind != ScheduledKind.OneShot)
                return true;

            return _oneShots.TryGetValue(entry.Name, out var current)
                   && ReferenceEquals(current, entry);
        }

        private void NoteStaleOneShotQueuedLocked()
        {
            if (_staleOneShotNodes < int.MaxValue)
                _staleOneShotNodes++;
        }

        private void NoteStaleEntryDequeuedLocked(ScheduledEntry entry)
        {
            if (entry != null && entry.Kind == ScheduledKind.OneShot && _staleOneShotNodes > 0)
                _staleOneShotNodes--;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            if (value.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value;
        }

        private static TimeSpan NormalizeDelay(TimeSpan delay)
            => delay <= TimeSpan.Zero ? TimeSpan.Zero : delay;

        private static async Task RunAsyncCallback(
            string name,
            Func<DateTime, Task> callback,
            DateTime utcNow)
        {
            try
            {
                await callback(utcNow);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Clock] async callback '{name}' error: {ex.Message}");
            }
        }

        private static DateTime NextMinuteBoundaryUtc(DateTime utcNow)
        {
            var minuteTicks = utcNow.Ticks / TimeSpan.TicksPerMinute;
            return new DateTime((minuteTicks + 1) * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        }

        private static void Invoke(string name, Action<DateTime> callback, DateTime utcNow)
        {
            try
            {
                callback(utcNow);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Clock] callback '{name}' error: {ex.Message}");
            }
        }

        private static DateTime ComputeNextDueUtc(MomentEntry moment, DateTime utcNow)
        {
            var beijing = utcNow.AddHours(TimeZoneOffsetHours);
            var candidate = beijing.Date.AddHours(moment.Hour).AddMinutes(moment.Minute);
            if (moment.Day.HasValue)
            {
                var forwardDays = ((int)moment.Day.Value - (int)candidate.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(forwardDays);
            }

            var stepDays = moment.Day.HasValue ? 7 : 1;
            while (candidate <= beijing)
                candidate = candidate.AddDays(stepDays);

            return DateTime.SpecifyKind(candidate.AddHours(-TimeZoneOffsetHours), DateTimeKind.Utc);
        }

        private sealed class TimeQueue<T>
            where T : class
        {
            private readonly PriorityQueue<T, QueuePriority> _heap
                = new PriorityQueue<T, QueuePriority>();
            private long _sequence;

            public int Count => _heap.Count;

            public void Enqueue(T item, DateTime dueUtc)
                => _heap.Enqueue(item, new QueuePriority(dueUtc.Ticks, _sequence++));

            public void Compact(Predicate<T> keep)
            {
                if (keep == null) throw new ArgumentNullException(nameof(keep));

                var live = new List<KeyValuePair<T, QueuePriority>>();
                foreach (var item in _heap.UnorderedItems)
                {
                    if (keep(item.Element))
                        live.Add(new KeyValuePair<T, QueuePriority>(item.Element, item.Priority));
                }

                _heap.Clear();
                foreach (var item in live)
                    _heap.Enqueue(item.Key, item.Value);
            }

            public bool TryPeek(out T item, out DateTime dueUtc)
            {
                if (_heap.TryPeek(out item, out var priority))
                {
                    dueUtc = new DateTime(priority.DueTicks, DateTimeKind.Utc);
                    return true;
                }

                dueUtc = default;
                return false;
            }

            public T Dequeue()
                => _heap.Dequeue();
        }

        private readonly struct QueuePriority : IComparable<QueuePriority>
        {
            public readonly long DueTicks;
            private readonly long _sequence;

            public QueuePriority(long dueTicks, long sequence)
            {
                DueTicks = dueTicks;
                _sequence = sequence;
            }

            public int CompareTo(QueuePriority other)
            {
                var dueCompare = DueTicks.CompareTo(other.DueTicks);
                return dueCompare != 0 ? dueCompare : _sequence.CompareTo(other._sequence);
            }
        }
    }
}
