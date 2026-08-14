using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DfoServer
{
    /// <summary>
    /// 将业务线程产生的日志放入内存队列，再由单个后台任务按入队顺序写入文件。
    /// 调用方不再等待磁盘 I/O；后台仍逐条使用 AppendAllText，不长期持有文件句柄，
    /// 因而只改变写入时机，不改变原有日志文件的打开、追加和关闭方式。
    /// </summary>
    public static class FileLogger
    {
        private static readonly string _logPath;
        private static readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        private static readonly Channel<string> _queue;
        private static readonly Task _consumerTask;
        private static int _shutdownStarted;

        static FileLogger()
        {
            var dir = AppContext.BaseDirectory;
            _logPath = Path.Combine(dir, "server.log");
            // server.log 包含中文诊断字段，显式写入 BOM 方便 Windows 查看器识别 UTF-8。
            File.WriteAllText(_logPath, $"=== DfoServer started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n", _encoding);

            // 单人运行时日志量有明确上限，使用无界队列可确保业务线程只负责入队，
            // 不会因为队列容量不足重新等待磁盘。单消费者负责维持日志的先后顺序。
            _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _consumerTask = Task.Run(ProcessQueueAsync);

            // 正常退出由 Program 主动等待队列排空；这里补充覆盖 Environment.Exit 等提前退出路径。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(TimeSpan.FromSeconds(2));
        }

        public static void Log(string message)
        {
            // 时间戳在调用线程生成，记录事件实际发生的时刻，而不是后台任务稍后落盘的时刻。
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n";
            if (!_queue.Writer.TryWrite(line))
                Console.Error.WriteLine("[FileLogger] logger has stopped; log entry dropped.");
        }

        public static void Log(string format, params object[] args)
        {
            Log(string.Format(format, args));
        }

        /// <summary>
        /// 停止接收新日志，并等待后台任务把已经入队的日志写完。
        /// 关闭只执行一次，避免 Program 和 ProcessExit 重复等待同一个后台任务。
        /// </summary>
        public static void Shutdown(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
                return;

            _queue.Writer.TryComplete();

            try
            {
                if (!_consumerTask.Wait(timeout))
                    Console.Error.WriteLine("[FileLogger] shutdown timed out before pending logs were written.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FileLogger] shutdown wait failed: {ex.Message}");
            }
        }

        private static async Task ProcessQueueAsync()
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    // 仅后台消费者访问日志文件，因此无需额外加锁；每次写完仍立即关闭文件句柄。
                    File.AppendAllText(_logPath, line, _encoding);
                }
                catch (Exception ex)
                {
                    // 不能调用 FileLogger.Log 记录自身错误，否则会再次入队并形成递归。
                    // 单条写入失败不终止消费者，后续日志仍有机会正常落盘。
                    Console.Error.WriteLine($"[FileLogger] append failed: {ex.Message}");
                }
            }
        }
    }
}
