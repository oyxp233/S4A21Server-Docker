using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    /// <summary>
    /// df_relay 式 UDP 哑中继(联机阶段3: 副本内实时同步的数据面)。
    /// 内容无关: 不解析副本内 UDP 数据报, 只按"房间"把一个成员发来的数据报转发给同房间其它成员。
    /// 房间成员与 UDP 端点由 TCP 控制面登记(JoinRoom / RegisterEndpoint); 转发按登记的完整 ip:port 路由。
    ///
    /// ⚠️ 尚未接线到服务端主流程 —— 需先逆向客户端拿到 UDP_HOST(0x001A)"可用主机"字节格式 +
    ///    SET_UDP_IP_PORT 格式(见 docs/联机进度), 才能让客户端真的往这里发包。本类是"协议无关的转发地基",
    ///    可用合成 UDP 独立自测(--selftest-udp-relay)。
    /// </summary>
    public sealed class UdpRelayServer : IDisposable
    {
        private sealed class Member
        {
            public Guid SessionId;
            public int RoomId;
            public IPEndPoint Endpoint; // 可空: 已入房但尚未登记 UDP 端点
        }

        private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Member>> _rooms = new();
        private readonly ConcurrentDictionary<Guid, Member> _members = new();            // sessionId -> member
        private readonly ConcurrentDictionary<IPEndPoint, Member> _endpointIndex = new();// ip:port -> member(入站 O(1) 归属)

        private UdpClient _udp;
        private CancellationTokenSource _cts;

        public int Port { get; }
        public long ForwardedDatagrams { get; private set; }

        public UdpRelayServer(int port)
        {
            Port = port;
        }

        public void Start()
        {
            if (_udp != null) return;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            _cts = new CancellationTokenSource();
            _ = ReceiveLoopAsync(_cts.Token);
            FileLogger.Log($"[UdpRelay] listening UDP {Port}");
        }

        // ===== 控制面登记(由 TCP 侧调用: 进副本 JoinRoom, SET_UDP_IP_PORT/源地址 RegisterEndpoint, 断线/退副本 Leave) =====

        public void JoinRoom(int roomId, Guid sessionId)
        {
            var m = _members.GetOrAdd(sessionId, _ => new Member { SessionId = sessionId, RoomId = roomId });
            if (m.RoomId != roomId)
            {
                RemoveFromRoom(m);
                m.RoomId = roomId;
            }
            _rooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<Guid, Member>())[sessionId] = m;
        }

        public void RegisterEndpoint(Guid sessionId, IPEndPoint endpoint)
        {
            if (endpoint == null || !_members.TryGetValue(sessionId, out var m))
                return;
            if (m.Endpoint != null)
                _endpointIndex.TryRemove(m.Endpoint, out _);
            m.Endpoint = endpoint;
            _endpointIndex[endpoint] = m;
        }

        public void Leave(Guid sessionId)
        {
            if (_members.TryRemove(sessionId, out var m))
            {
                RemoveFromRoom(m);
                if (m.Endpoint != null)
                    _endpointIndex.TryRemove(m.Endpoint, out _);
            }
        }

        private void RemoveFromRoom(Member m)
        {
            if (_rooms.TryGetValue(m.RoomId, out var room))
            {
                room.TryRemove(m.SessionId, out _);
                if (room.IsEmpty)
                    _rooms.TryRemove(m.RoomId, out _);
            }
        }

        // ===== 转发决策(纯逻辑, 可测) =====

        /// <summary>给定数据报来源端点, 返回应转发到的目标端点(同房间其它已登记端点的成员)。来源未登记则空。</summary>
        public IReadOnlyList<IPEndPoint> ResolveTargets(IPEndPoint source)
        {
            var result = new List<IPEndPoint>();
            if (source == null || !_endpointIndex.TryGetValue(source, out var src))
                return result;
            if (!_rooms.TryGetValue(src.RoomId, out var room))
                return result;
            foreach (var kv in room)
            {
                var m = kv.Value;
                if (m.SessionId == src.SessionId || m.Endpoint == null)
                    continue;
                result.Add(m.Endpoint);
            }
            return result;
        }

        public int RoomCount => _rooms.Count;

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception) { continue; }

                foreach (var ep in ResolveTargets(r.RemoteEndPoint))
                {
                    try { await _udp.SendAsync(r.Buffer, r.Buffer.Length, ep); ForwardedDatagrams++; }
                    catch { }
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _udp?.Dispose();
        }
    }
}
