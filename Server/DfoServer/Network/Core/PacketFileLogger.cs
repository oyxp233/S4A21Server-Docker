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
        private static readonly object _lock = new object();
        private static string _logPath;
        private static bool _enabled = false;
        private static int _bestEffortBatchActive;

        public static void Initialize()
        {
            _enabled = GameNetworkConfig.PacketCaptureEnabled;
            if (!_enabled) return;

            var dir = GameNetworkConfig.PacketCaptureDir ?? AppContext.BaseDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "packet_log.txt");
            File.WriteAllText(_logPath, $"=== DfoServer packet capture started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
            File.AppendAllText(_logPath, $"=== A21 SEND=15B header, RECV=14B header. Body offset differs by direction. ===\r\n\r\n");
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

            lock (_lock)
            {
                File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
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
    }
}
