using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    /// <summary>
    /// 组队/副本 UDP 中继(TURN 式, per-ordered-pair)。公网组队 NAT 穿透:队友之间【不】互相直连,
    /// 而是各自把"发给队友 P"的 P2P UDP 发到【服务器公网 IP : 该(成员→队友)对专属端口】, relay 按对转发。
    ///
    /// 为什么 per-ordered-pair(每有序对一个 socket)而不是单口/每成员一口:
    ///   ① 客户端按【源 ip:port】区分是哪个队友发来的。所有队友都指到同一服务器端口 → 客户端无法区分。
    ///   ② 对称 NAT 下同一客户端发往【不同目标端口】会得到【不同外部映射端口】→ 靠源端点匹配"发送者"会错乱。
    ///   每有序对独立 socket:数据报到达 socket 本身就唯一确定 (owner→peer),无需跨端口匹配 → 对称 NAT 也鲁棒。
    ///
    /// 每条腿 socket(owner,peer):owner 往它发(去找 peer)、也从它收(peer 发来的)。relay 收到 owner 的包后,
    ///   从 socket(peer,owner) 把负载发给 peer 的已学真实端点。owner 的真实端点 = 本腿上收到的首包源(每腿独立学)。
    /// 客户端视角:队友 P 恒在 服务器公网IP:portOwnerPeer,收发对称,relay 全透明。
    ///
    /// ⚠️ 需真机验证(DNF 铁律:服务端绿≠客户端认):客户端被 0x0B 指到 relay 端点后是否真往那发 UDP。
    ///    env DFO_UDP_RELAY 门控;关(默认)时本类根本不被实例化, LAN 直连行为完全不变。
    /// 端口从固定范围 [PortBase, PortBase+PortCount) 分配(云安全组按此范围放行 UDP)。
    /// </summary>
    public sealed class PartyUdpRelay : IDisposable
    {
        public readonly struct MemberBinding
        {
            public MemberBinding(
                int memberKey,
                Guid sessionId,
                IPAddress expectedOwnerIp)
            {
                MemberKey = memberKey;
                SessionId = sessionId;
                ExpectedOwnerIp = NormalizeAddress(
                    expectedOwnerIp);
            }

            public int MemberKey { get; }
            public Guid SessionId { get; }
            public IPAddress ExpectedOwnerIp { get; }
        }

        public sealed class RoomSnapshot
        {
            private readonly IReadOnlyDictionary<(int Owner, int Peer), int>
                _ports;

            internal RoomSnapshot(
                int roomId,
                long generation,
                bool secureBindings,
                IReadOnlyDictionary<(int Owner, int Peer), int> ports)
            {
                RoomId = roomId;
                Generation = generation;
                SecureBindings = secureBindings;
                _ports = ports;
            }

            public int RoomId { get; }
            public long Generation { get; }
            public bool SecureBindings { get; }

            public bool TryGetPort(
                int ownerKey, int peerKey, out int port)
            {
                return _ports.TryGetValue(
                    (ownerKey, peerKey), out port);
            }
        }

        private sealed class Leg
        {
            public int RoomId;
            public int OwnerKey;
            public int PeerKey;
            public Guid OwnerSessionId;
            public Guid PeerSessionId;
            public IPAddress ExpectedOwnerIp;
            public IPAddress ExpectedPeerIp;
            public bool SecureBinding;
            public UdpClient Sock;
            public int Port;
            public volatile IPEndPoint LearnedOwnerEp;   // owner 在此腿上的真实源端点(首包学到, 每腿独立)
            public bool FirstRecvLogged;                 // 诊断: 只在收到客户端首包时打一次日志
            public bool FirstFwdLogged;                  // 诊断: 只在首次转发成功时打一次日志
            public bool FirstRejectedSourceLogged;
        }

        private sealed class RoomState
        {
            public long Generation;
            public bool SecureBindings;
            public IReadOnlyList<int> MemberKeys;
            public IReadOnlyDictionary<int, MemberBinding> Bindings;
        }

        private const int MaxSecureMembers = 8;
        private const int MaxDatagramBytes = 2048;
        private readonly string _publicIp;
        private readonly string _scope;
        private readonly int _portBase;
        private readonly int _portCount;
        private readonly int _initialPortCursor;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly Dictionary<int, Dictionary<(int, int), Leg>> _roomLegs = new();   // roomId -> (owner,peer) -> Leg
        private readonly Dictionary<int, RoomState> _roomStates = new();
        private readonly HashSet<int> _usedPorts = new();
        private int _nextPortCursor;
        private long _nextRoomGeneration;
        private long _rejectedSourceDatagrams;
        private bool _disposed;

        public string PublicIp => _publicIp;
        public string Scope => _scope;
        public int PortBase => _portBase;
        public int PortCount => _portCount;
        internal int InitialPortCursorForTest =>
            _initialPortCursor;
        public long RejectedSourceDatagrams =>
            Interlocked.Read(ref _rejectedSourceDatagrams);
        public long ForwardedDatagrams;   // 诊断计数

        public PartyUdpRelay(
            string publicIp,
            int portBase,
            int portCount,
            string scope = "party")
        {
            _publicIp = string.IsNullOrWhiteSpace(publicIp) ? "127.0.0.1" : publicIp.Trim();
            _scope = NormalizeScope(scope);
            _portBase = portBase > 0 ? portBase : 30000;
            _portCount = Math.Max(2, portCount);
            _nextPortCursor =
                RandomNumberGenerator.GetInt32(_portCount);
            _initialPortCursor = _nextPortCursor;
            FileLogger.Log(
                $"[PartyUdpRelay scope={_scope}] enabled " +
                $"portRange={_portBase}.." +
                $"{_portBase + _portCount - 1}/udp");
        }

        /// <summary>
        /// 兼容旧调用方。需要判断完整矩阵是否建立成功时应使用
        /// TrySyncRoom。
        /// </summary>
        public void SyncRoom(int roomId, IReadOnlyList<int> memberKeys)
        {
            TrySyncRoom(roomId, memberKeys, out _);
        }

        /// <summary>
        /// 以增量方式把房间调整为给定成员集合。仍在队伍中的有向腿会保留
        /// 原 socket、端口和已学习端点；只创建缺失腿、删除离队成员相关腿。
        /// 新腿必须全部分配成功才会一次性发布，否则回滚新分配并保留旧矩阵。
        /// </summary>
        public bool TrySyncRoom(
            int roomId,
            IReadOnlyList<int> memberKeys,
            out RoomSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = (memberKeys ?? Array.Empty<int>())
                .Where(key => key > 0)
                .Distinct()
                .OrderBy(key => key)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return TrySyncRoomCore(
                roomId,
                members,
                secureBindings: null,
                secure: false,
                out snapshot,
                cancellationToken);
        }

        /// <summary>
        /// Secure PvP overload. Every member is bound to the authenticated TCP
        /// session generation and its observed remote IPv4 address. Learned UDP
        /// tuples survive only while both endpoint bindings for that ordered
        /// pair are unchanged.
        /// </summary>
        public bool TrySyncRoom(
            int roomId,
            IReadOnlyList<MemberBinding> memberBindings,
            out RoomSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            snapshot = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeSecureBindings(
                    memberBindings,
                    out var bindings))
            {
                return false;
            }

            return TrySyncRoomCore(
                roomId,
                bindings.Keys.OrderBy(key => key).ToArray(),
                bindings,
                secure: true,
                out snapshot,
                cancellationToken);
        }

        private bool TrySyncRoomCore(
            int roomId,
            IReadOnlyList<int> members,
            IReadOnlyDictionary<int, MemberBinding> secureBindings,
            bool secure,
            out RoomSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            snapshot = null;
            lock (_gate)
            {
                if (_disposed || roomId <= 0)
                    return false;

                if (members.Count < 2)
                {
                    CloseRoomLocked(roomId);
                    return false;
                }

                _roomLegs.TryGetValue(roomId, out var currentLegs);
                currentLegs ??= new Dictionary<(int, int), Leg>();
                _roomStates.TryGetValue(roomId, out var currentState);
                var generation =
                    RoomStateMatches(
                        currentState,
                        members,
                        secureBindings,
                        secure)
                        ? currentState.Generation
                        : NextRoomGenerationLocked();

                var requiredPairs = new HashSet<(int Owner, int Peer)>();
                foreach (var owner in members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var peer in members)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (owner != peer)
                            requiredPairs.Add((owner, peer));
                    }
                }

                var nextLegs =
                    new Dictionary<(int, int), Leg>(
                        requiredPairs.Count);
                int preservedLegCount = currentLegs.Count(
                    pair => requiredPairs.Contains(pair.Key));
                var newLegs = new List<Leg>();
                var reboundLegs =
                    new List<(
                        Leg Leg,
                        MemberBinding Owner,
                        MemberBinding Peer)>();
                try
                {
                    foreach (var pair in requiredPairs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (currentLegs.TryGetValue(pair, out var existingLeg))
                        {
                            nextLegs[pair] = existingLeg;
                            if (secure)
                            {
                                var ownerBinding =
                                    secureBindings[pair.Owner];
                                var peerBinding =
                                    secureBindings[pair.Peer];
                                if (!LegBindingsMatch(
                                        existingLeg,
                                        ownerBinding,
                                        peerBinding))
                                {
                                    reboundLegs.Add(
                                        (
                                            existingLeg,
                                            ownerBinding,
                                            peerBinding));
                                }
                            }
                            else if (existingLeg.SecureBinding)
                            {
                                reboundLegs.Add(
                                    (
                                        existingLeg,
                                        default,
                                        default));
                            }
                            continue;
                        }

                        var newLeg = OpenLegLocked(
                            roomId,
                            pair.Owner,
                            pair.Peer,
                            cancellationToken);
                        if (newLeg == null)
                        {
                            foreach (var candidate in newLegs)
                                DisposeLegLocked(candidate);

                            FileLogger.Log(
                                $"[PartyUdpRelay scope={_scope}] " +
                                $"SyncRoom rejected room={roomId} " +
                                $"members=[{string.Join(",", members)}] " +
                                $"need={requiredPairs.Count} " +
                                $"preserved={preservedLegCount} " +
                                $"available={_portCount - _usedPorts.Count}");
                            return false;
                        }

                        nextLegs[pair] = newLeg;
                        if (secure)
                        {
                            reboundLegs.Add(
                                (
                                    newLeg,
                                    secureBindings[pair.Owner],
                                    secureBindings[pair.Peer]));
                        }
                        newLegs.Add(newLeg);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    foreach (var candidate in newLegs)
                        DisposeLegLocked(candidate);
                    throw;
                }

                var obsoleteLegs = currentLegs
                    .Where(pair => !requiredPairs.Contains(pair.Key))
                    .Select(pair => pair.Value)
                    .ToList();

                // 先发布完整矩阵，再启动新腿接收循环。接收循环在锁内无法
                // 观察到半成品；旧腿也会在同一个临界区内失效并释放。
                foreach (var rebound in reboundLegs)
                {
                    if (secure)
                    {
                        ApplySecureBindingLocked(
                            rebound.Leg,
                            rebound.Owner,
                            rebound.Peer);
                    }
                    else
                    {
                        ApplyLegacyBindingLocked(rebound.Leg);
                    }
                }
                _roomLegs[roomId] = nextLegs;
                _roomStates[roomId] =
                    new RoomState
                    {
                        Generation = generation,
                        SecureBindings = secure,
                        MemberKeys = members.ToArray(),
                        Bindings = secureBindings == null
                            ? null
                            : new Dictionary<int, MemberBinding>(
                                secureBindings)
                    };
                foreach (var obsoleteLeg in obsoleteLegs)
                    DisposeLegLocked(obsoleteLeg);
                foreach (var newLeg in newLegs)
                    _ = ReceiveLoopAsync(newLeg, _cts.Token);

                snapshot = CreateSnapshot(
                    roomId,
                    generation,
                    secure,
                    nextLegs);
                FileLogger.Log(
                    $"[PartyUdpRelay scope={_scope}] SyncRoom room={roomId} " +
                    $"members=[{string.Join(",", members)}] " +
                    $"generation={generation} secure={secure} " +
                    $"legs={nextLegs.Count} " +
                    $"preserved={nextLegs.Count - newLegs.Count} " +
                    $"added={newLegs.Count} removed={obsoleteLegs.Count}");
                return true;
            }
        }

        /// <summary>
        /// owner 应被告知“peer 在服务器公网 IP:此端口”。返回 0 表示
        /// 矩阵不完整；调用方不得把它与中继 IP 组合后下发。
        /// </summary>
        public int GetPort(int roomId, int ownerKey, int peerKey)
        {
            lock (_gate)
            {
                if (_roomLegs.TryGetValue(roomId, out var legs) && legs.TryGetValue((ownerKey, peerKey), out var leg))
                    return leg.Port;
                return 0;
            }
        }

        /// <summary>
        /// Forgets every source tuple learned on legs owned by one member.
        /// The room matrix, sockets and advertised ports stay unchanged, and
        /// endpoints learned for every other owner remain valid. The next
        /// datagram sent by this owner to each leg learns its new tuple.
        /// </summary>
        public bool ResetMemberEndpoints(int roomId, int ownerKey)
        {
            lock (_gate)
            {
                if (_disposed ||
                    roomId <= 0 ||
                    ownerKey <= 0 ||
                    (_roomStates.TryGetValue(
                         roomId,
                         out var state) &&
                     state.SecureBindings) ||
                    !_roomLegs.TryGetValue(roomId, out var legs))
                {
                    return false;
                }

                int resetCount = 0;
                foreach (var leg in legs.Values)
                {
                    if (leg.OwnerKey != ownerKey)
                        continue;

                    leg.LearnedOwnerEp = null;
                    leg.FirstRejectedSourceLogged = false;
                    resetCount++;
                }

                if (resetCount == 0)
                    return false;

                FileLogger.Log(
                    $"[PartyUdpRelay scope={_scope}] " +
                    $"ResetMemberEndpoints room={roomId} " +
                    $"owner={ownerKey} legs={resetCount}");
                return true;
            }
        }

        /// <summary>
        /// Authenticated reset for a secure room. A stale TCP generation cannot
        /// clear tuples belonging to the replacement session.
        /// </summary>
        public bool ResetMemberEndpoints(
            int roomId,
            int ownerKey,
            Guid ownerSessionId)
        {
            if (ownerSessionId == Guid.Empty)
                return false;

            lock (_gate)
            {
                if (_disposed ||
                    !_roomStates.TryGetValue(
                        roomId,
                        out var state) ||
                    !state.SecureBindings ||
                    state.Bindings == null ||
                    !state.Bindings.TryGetValue(
                        ownerKey,
                        out var binding) ||
                    binding.SessionId != ownerSessionId ||
                    !_roomLegs.TryGetValue(roomId, out var legs))
                {
                    return false;
                }

                int resetCount = 0;
                foreach (var leg in legs.Values)
                {
                    if (leg.OwnerKey != ownerKey ||
                        leg.OwnerSessionId != ownerSessionId)
                    {
                        continue;
                    }

                    ForgetLearnedEndpointLocked(leg);
                    resetCount++;
                }

                if (resetCount == 0)
                    return false;

                FileLogger.Log(
                    $"[PartyUdpRelay scope={_scope}] " +
                    $"ResetMemberEndpoints room={roomId} " +
                    $"owner={ownerKey} legs={resetCount} secure=True");
                return true;
            }
        }

        public void CloseRoom(int roomId)
        {
            lock (_gate) CloseRoomLocked(roomId);
        }

        public int RoomCount { get { lock (_gate) return _roomLegs.Count; } }

        private void CloseRoomLocked(int roomId)
        {
            if (_roomLegs.TryGetValue(roomId, out var legs))
            {
                foreach (var leg in legs.Values)
                    DisposeLegLocked(leg);
                _roomLegs.Remove(roomId);
                _roomStates.Remove(roomId);
                FileLogger.Log($"[PartyUdpRelay scope={_scope}] CloseRoom room={roomId} legs={legs.Count}");
            }
            else
            {
                _roomStates.Remove(roomId);
            }
        }

        private static RoomSnapshot CreateSnapshot(
            int roomId,
            long generation,
            bool secureBindings,
            IReadOnlyDictionary<(int, int), Leg> legs)
        {
            var ports =
                new Dictionary<(int Owner, int Peer), int>(
                    legs.Count);
            foreach (var pair in legs)
                ports[pair.Key] = pair.Value.Port;
            return new RoomSnapshot(
                roomId,
                generation,
                secureBindings,
                ports);
        }

        private long NextRoomGenerationLocked()
        {
            _nextRoomGeneration++;
            if (_nextRoomGeneration <= 0)
                _nextRoomGeneration = 1;
            return _nextRoomGeneration;
        }

        private static bool RoomStateMatches(
            RoomState state,
            IReadOnlyList<int> members,
            IReadOnlyDictionary<int, MemberBinding> secureBindings,
            bool secure)
        {
            if (state == null ||
                state.SecureBindings != secure ||
                state.MemberKeys == null ||
                state.MemberKeys.Count != members.Count)
            {
                return false;
            }

            for (int index = 0; index < members.Count; index++)
            {
                if (state.MemberKeys[index] != members[index])
                    return false;
            }

            if (!secure)
                return true;
            if (state.Bindings == null ||
                secureBindings == null ||
                state.Bindings.Count != secureBindings.Count)
            {
                return false;
            }

            foreach (var pair in secureBindings)
            {
                if (!state.Bindings.TryGetValue(
                        pair.Key,
                        out var current) ||
                    !BindingsEqual(current, pair.Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryNormalizeSecureBindings(
            IReadOnlyList<MemberBinding> source,
            out IReadOnlyDictionary<int, MemberBinding> bindings)
        {
            bindings = null;
            if (source == null ||
                source.Count < 2 ||
                source.Count > MaxSecureMembers)
            {
                return false;
            }

            var normalized =
                new Dictionary<int, MemberBinding>(source.Count);
            foreach (var candidate in source)
            {
                var address = NormalizeAddress(
                    candidate.ExpectedOwnerIp);
                if (candidate.MemberKey <= 0 ||
                    candidate.SessionId == Guid.Empty ||
                    !IsConcreteIpv4(address) ||
                    normalized.ContainsKey(candidate.MemberKey))
                {
                    return false;
                }

                normalized[candidate.MemberKey] =
                    new MemberBinding(
                        candidate.MemberKey,
                        candidate.SessionId,
                        address);
            }

            bindings = normalized;
            return true;
        }

        private static bool IsConcreteIpv4(IPAddress address)
        {
            if (address == null ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.None) ||
                address.Equals(IPAddress.Broadcast))
            {
                return false;
            }

            var firstOctet = address.GetAddressBytes()[0];
            return firstOctet >= 1 && firstOctet < 224;
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            if (address == null)
                return null;
            return address.IsIPv4MappedToIPv6
                ? address.MapToIPv4()
                : address;
        }

        private static bool BindingsEqual(
            MemberBinding left,
            MemberBinding right)
        {
            return left.MemberKey == right.MemberKey &&
                   left.SessionId == right.SessionId &&
                   Equals(
                       left.ExpectedOwnerIp,
                       right.ExpectedOwnerIp);
        }

        private static bool LegBindingsMatch(
            Leg leg,
            MemberBinding owner,
            MemberBinding peer)
        {
            return leg != null &&
                   leg.SecureBinding &&
                   leg.OwnerKey == owner.MemberKey &&
                   leg.PeerKey == peer.MemberKey &&
                   leg.OwnerSessionId == owner.SessionId &&
                   leg.PeerSessionId == peer.SessionId &&
                   Equals(
                       leg.ExpectedOwnerIp,
                       owner.ExpectedOwnerIp) &&
                   Equals(
                       leg.ExpectedPeerIp,
                       peer.ExpectedOwnerIp);
        }

        private static void ApplySecureBindingLocked(
            Leg leg,
            MemberBinding owner,
            MemberBinding peer)
        {
            leg.SecureBinding = true;
            leg.OwnerSessionId = owner.SessionId;
            leg.PeerSessionId = peer.SessionId;
            leg.ExpectedOwnerIp = owner.ExpectedOwnerIp;
            leg.ExpectedPeerIp = peer.ExpectedOwnerIp;
            ForgetLearnedEndpointLocked(leg);
        }

        private static void ApplyLegacyBindingLocked(Leg leg)
        {
            leg.SecureBinding = false;
            leg.OwnerSessionId = Guid.Empty;
            leg.PeerSessionId = Guid.Empty;
            leg.ExpectedOwnerIp = null;
            leg.ExpectedPeerIp = null;
            ForgetLearnedEndpointLocked(leg);
        }

        private static void ForgetLearnedEndpointLocked(Leg leg)
        {
            leg.LearnedOwnerEp = null;
            leg.FirstRecvLogged = false;
            leg.FirstFwdLogged = false;
            leg.FirstRejectedSourceLogged = false;
        }

        private void DisposeLegLocked(Leg leg)
        {
            try { leg?.Sock?.Dispose(); } catch { }
            if (leg != null)
                _usedPorts.Remove(leg.Port);
        }

        private Leg OpenLegLocked(
            int roomId,
            int ownerKey,
            int peerKey,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < _portCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int port = _portBase + ((_nextPortCursor + i) % _portCount);
                if (_usedPorts.Contains(port)) continue;
                try
                {
                    var sock = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                    _usedPorts.Add(port);
                    _nextPortCursor = (port - _portBase + 1) % _portCount;
                    var leg = new Leg { RoomId = roomId, OwnerKey = ownerKey, PeerKey = peerKey, Sock = sock, Port = port };
                    return leg;
                }
                catch (SocketException) { /* 端口占用, 试范围内下一个 */ }
            }
            FileLogger.Log($"[PartyUdpRelay scope={_scope}] OpenLeg 失败: 端口范围 {_portBase}..{_portBase + _portCount - 1} 用尽 room={roomId} {ownerKey}->{peerKey}");
            return null;
        }

        private async Task ReceiveLoopAsync(Leg leg, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await leg.Sock.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }   // 瞬时 ICMP 端口不可达等, 继续
                catch (Exception) { break; }

                if (r.Buffer.Length > MaxDatagramBytes)
                {
                    Interlocked.Increment(
                        ref _rejectedSourceDatagrams);
                    continue;
                }

                // 桥接: owner→peer 的包, 从 socket(peer,owner) 发给 peer 的已学端点
                Leg bridge = null;
                IPEndPoint dst = null;
                Task<int> forwardTask = null;
                bool firstReceive = false;
                bool rejectedSource = false;
                bool logRejectedSource = false;
                lock (_gate)
                {
                    if (!_roomLegs.TryGetValue(
                            leg.RoomId, out var legs) ||
                        !legs.TryGetValue(
                            (leg.OwnerKey, leg.PeerKey),
                            out var currentLeg) ||
                        !ReferenceEquals(currentLeg, leg))
                    {
                        continue;
                    }

                    if (!TryAcceptSourceLocked(
                            leg,
                            r.RemoteEndPoint,
                            out firstReceive,
                            out logRejectedSource))
                    {
                        rejectedSource = true;
                        Interlocked.Increment(
                            ref _rejectedSourceDatagrams);
                    }

                    if (!rejectedSource &&
                        legs.TryGetValue(
                            (leg.PeerKey, leg.OwnerKey),
                            out bridge))
                    {
                        dst = bridge.LearnedOwnerEp;
                        if (dst != null)
                        {
                            try
                            {
                                // Start the send while holding the same gate
                                // used by ResetMemberEndpoints. A reset either
                                // clears the destination before this decision,
                                // or linearizes after this datagram was sent.
                                forwardTask = bridge.Sock.SendAsync(
                                    r.Buffer,
                                    r.Buffer.Length,
                                    dst);
                            }
                            catch
                            {
                                forwardTask = null;
                            }
                        }
                    }
                }

                if (firstReceive)
                {
                    FileLogger.Log(
                        $"[PartyUdpRelay scope={_scope}] " +
                        $"room={leg.RoomId} " +
                        $"owner={leg.OwnerKey}->peer={leg.PeerKey} " +
                        "accepted first UDP source tuple");
                }

                if (logRejectedSource)
                {
                    FileLogger.Log(
                        $"[PartyUdpRelay scope={_scope}] " +
                        $"rejected source tuple room={leg.RoomId} " +
                        $"owner={leg.OwnerKey}->peer={leg.PeerKey}");
                }

                if (rejectedSource || forwardTask == null)
                    continue;

                try
                {
                    await forwardTask;
                    Interlocked.Increment(ref ForwardedDatagrams);
                    if (!leg.FirstFwdLogged)
                    {
                        leg.FirstFwdLogged = true;
                        FileLogger.Log(
                            $"[PartyUdpRelay scope={_scope}] " +
                            $"room={leg.RoomId} " +
                            $"owner={leg.OwnerKey}->peer={leg.PeerKey} " +
                            "first relay forward succeeded");
                    }
                }
                catch { }
            }
        }

        private bool TryAcceptSourceLocked(
            Leg leg,
            IPEndPoint source,
            out bool firstReceive,
            out bool firstRejected)
        {
            firstReceive = false;
            firstRejected = false;
            if (leg == null || source == null)
                return false;

            var sourceAddress = NormalizeAddress(source.Address);
            if (leg.SecureBinding &&
                !Equals(leg.ExpectedOwnerIp, sourceAddress))
            {
                if (!leg.FirstRejectedSourceLogged)
                {
                    leg.FirstRejectedSourceLogged = true;
                    firstRejected = true;
                }
                return false;
            }

            var normalizedSource =
                new IPEndPoint(sourceAddress, source.Port);
            if (leg.LearnedOwnerEp == null)
            {
                leg.LearnedOwnerEp = normalizedSource;
                if (!leg.FirstRecvLogged)
                {
                    leg.FirstRecvLogged = true;
                    firstReceive = true;
                }
                return true;
            }

            if (EndpointsEqual(
                    leg.LearnedOwnerEp,
                    normalizedSource))
            {
                return true;
            }

            if (!leg.FirstRejectedSourceLogged)
            {
                leg.FirstRejectedSourceLogged = true;
                firstRejected = true;
            }
            return false;
        }

        internal bool TryAcceptSourceForTest(
            int roomId,
            int ownerKey,
            int peerKey,
            IPEndPoint source)
        {
            lock (_gate)
            {
                if (!_roomLegs.TryGetValue(roomId, out var legs) ||
                    !legs.TryGetValue(
                        (ownerKey, peerKey),
                        out var leg))
                {
                    return false;
                }

                var accepted = TryAcceptSourceLocked(
                    leg,
                    source,
                    out _,
                    out _);
                if (!accepted)
                {
                    Interlocked.Increment(
                        ref _rejectedSourceDatagrams);
                }
                return accepted;
            }
        }

        internal IPEndPoint GetLearnedEndpointForTest(
            int roomId,
            int ownerKey,
            int peerKey)
        {
            lock (_gate)
            {
                if (!_roomLegs.TryGetValue(roomId, out var legs) ||
                    !legs.TryGetValue(
                        (ownerKey, peerKey),
                        out var leg) ||
                    leg.LearnedOwnerEp == null)
                {
                    return null;
                }

                return new IPEndPoint(
                    leg.LearnedOwnerEp.Address,
                    leg.LearnedOwnerEp.Port);
            }
        }

        private static bool EndpointsEqual(
            IPEndPoint left,
            IPEndPoint right)
        {
            return left != null &&
                   right != null &&
                   left.Port == right.Port &&
                   left.Address.Equals(right.Address);
        }

        private static string NormalizeScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
                return "party";

            string normalized = scope
                .Trim()
                .Replace('\r', '_')
                .Replace('\n', '_');
            return normalized.Length <= 32
                ? normalized
                : normalized.Substring(0, 32);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _cts.Cancel();
                foreach (var roomId in _roomLegs.Keys.ToList())
                    CloseRoomLocked(roomId);
            }
            try { _cts.Dispose(); } catch { }
        }
    }
}
