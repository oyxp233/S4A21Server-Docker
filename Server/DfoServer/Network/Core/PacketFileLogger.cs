using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    public static class PacketFileLogger
    {
        private const int QueueCapacity = 4096;
        private const long MaxLogBytes = 64L * 1024L * 1024L;
        private static string _logPath;
        private static bool _enabled = false;
        private static BoundedAsyncLogQueue _queue;
        private static Task _consumerTask;
        private static int _shutdownStarted;
        private static long _lastDropNotice;
        private static int _bestEffortBatchActive;

        public static void Initialize()
        {
            _enabled = GameNetworkConfig.PacketCaptureEnabled;
            if (!_enabled) return;

            var dir = GameNetworkConfig.PacketCaptureDir ?? AppContext.BaseDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "packet_log.txt");
            File.WriteAllText(_logPath, BuildHeader(), new UTF8Encoding(false));
            _queue = new BoundedAsyncLogQueue(QueueCapacity);
            _consumerTask = Task.Run(ProcessQueueAsync);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Shutdown(TimeSpan.FromSeconds(2));
        }

        public static void Log(string direction, byte[] data)
        {
            if (!_enabled) return;
            if (data == null || data.Length < 3) return;

            var cmd = data[0];
            var type = (ushort)(data[1] | (data[2] << 8));

            // Server SEND is client inbound (15B); client RECV is server inbound (14B).
            int hdrSize = direction == "SEND" ? 15 : 14;
            int bodyLen = data.Length - hdrSize;
            if (bodyLen < 0) bodyLen = 0;

            var sb = new StringBuilder();
            sb.AppendFormat("{0} cmd=0x{1:X2} type=0x{2:X4} len={3} body=({4}B)",
                direction, cmd, type, data.Length, bodyLen);

            // Show body bytes as space-separated hex
            if (bodyLen > 0)
            {
                sb.Append(" [");
                for (int i = hdrSize; i < data.Length; i++)
                {
                    if (i > hdrSize) sb.Append(' ');
                    sb.AppendFormat("{0:X2}", data[i]);
                }
                sb.Append(']');
            }
            sb.Append("\r\n");

            // Full hex dump for detail (one line)
            sb.AppendFormat("  raw: {0}\r\n",
                BitConverter.ToString(data).Replace("-", " "));

            if (_queue.TryEnqueue(sb.ToString()))
                return;

            var dropped = _queue.DroppedCount;
            if (dropped == 1 || dropped % 1000 == 0)
            {
                var previous = Interlocked.Exchange(
                    ref _lastDropNotice,
                    dropped);
                if (previous != dropped)
                {
                    Console.Error.WriteLine(
                        $"[PacketCapture] bounded queue full; dropped={dropped}.");
                }
            }
        }

        public static void Shutdown(TimeSpan timeout)
        {
            if (!_enabled
                || Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            {
                return;
            }

            _queue?.TryComplete();
            try
            {
                if (_consumerTask != null && !_consumerTask.Wait(timeout))
                {
                    Console.Error.WriteLine(
                        "[PacketCapture] shutdown timed out before pending packets were written.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[PacketCapture] shutdown wait failed: {ex.Message}");
            }
        }

        internal static void LogBatchBestEffort(
            string direction,
            IReadOnlyList<byte[]> packets)
        {
            if (!_enabled || packets == null || packets.Count == 0)
                return;
            if (Interlocked.CompareExchange(
                    ref _bestEffortBatchActive,
                    1,
                    0) != 0)
            {
                return;
            }

            try
            {
                var snapshot = new byte[packets.Count][];
                for (var index = 0; index < packets.Count; index++)
                {
                    var packet = packets[index];
                    if (packet == null || packet.Length == 0)
                    {
                        Interlocked.Exchange(
                            ref _bestEffortBatchActive,
                            0);
                        return;
                    }
                    snapshot[index] = (byte[])packet.Clone();
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var packet in snapshot)
                            Log(direction, packet);
                    }
                    catch
                    {
                        // Packet capture must never affect the live transport.
                    }
                    finally
                    {
                        Interlocked.Exchange(
                            ref _bestEffortBatchActive,
                            0);
                    }
                });
            }
            catch
            {
                Interlocked.Exchange(ref _bestEffortBatchActive, 0);
            }
        }

        private static async Task ProcessQueueAsync()
        {
            FileStream stream = null;
            StreamWriter writer = null;
            try
            {
                OpenWriter(out stream, out writer);
                await foreach (var entry in _queue.Reader.ReadAllAsync()
                    .ConfigureAwait(false))
                {
                    var entryBytes = Encoding.UTF8.GetByteCount(entry);
                    if (stream.Length + entryBytes > MaxLogBytes)
                    {
                        writer.Dispose();
                        stream = null;
                        writer = null;
                        RotateLog();
                        OpenWriter(out stream, out writer);
                        await writer.WriteAsync(BuildHeader())
                            .ConfigureAwait(false);
                    }

                    await writer.WriteAsync(entry).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[PacketCapture] background writer failed: {ex.Message}");
            }
            finally
            {
                writer?.Dispose();
                stream?.Dispose();
            }
        }

        private static void OpenWriter(
            out FileStream stream,
            out StreamWriter writer)
        {
            stream = new FileStream(
                _logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                64 * 1024)
            {
                AutoFlush = true,
            };
        }

        private static void RotateLog()
        {
            var rotatedPath = _logPath + ".1";
            if (File.Exists(rotatedPath))
                File.Delete(rotatedPath);
            if (File.Exists(_logPath))
                File.Move(_logPath, rotatedPath);
        }

        private static string BuildHeader()
            => $"=== DfoServer packet capture started "
                + $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n"
                + "=== A21 SEND=15B header, RECV=14B header. "
                + "Body offset differs by direction. ===\r\n\r\n";
    }
}
