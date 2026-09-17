using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;

namespace DfoServer.Network
{
    public class EnhancedClientSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public TcpClient TcpClient { get; }
        public NetworkStream Stream => TcpClient.GetStream();
        public DateTime ConnectedTime { get; } = DateTime.Now;
        public IPacketHeader PacketStructure { get; private set; }
        public ushort SequenceNumber { get; private set; }

        public int ListenerPort { get; }

        
        
        
        
        public PlayerContext Player { get; } = new PlayerContext();

        
        
        
        public AccountRecord Account { get; set; }

        public GameSession GameSession { get; set; }

        // 玩家当前打开的收集箱 PVF [Index] 值(0388请求体末尾字节, 见 CollectionBoxHandler)
        public int SelectedCollectionBoxIndex { get; set; }

        public int PendingDarkKnightAutoComboCharacterId { get; set; }

        public DateTime PendingDarkKnightAutoComboUtc { get; set; }

        public int PendingReturnSelectCharacterId { get; set; }

        // A21 首次教程 GIVEUP_GAME 回城后，客户端会先发送一组
        // SYNC_ITEM_SPACE(0x035C) / STORY_PAUSE，再等待 VILLAGE_OBJECT_LIST。
        public bool A21TutorialReturnNeedsVillageObjectList { get; set; }

        // A21：00AD 后等待客户端保存两次 00C5，再发送登录成功。
        public bool A21LoginSuccessPending { get; set; }

        public int A21SelectOptionSaveCount { get; set; }

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private volatile bool _supportsRaidMemberColumnV1;

        internal bool SupportsRaidMemberColumnV1 => _supportsRaidMemberColumnV1;

        internal bool TrySetRaidMemberColumnProtocolVersion(uint version)
        {
            if (version != 1)
                return false;
            _supportsRaidMemberColumnV1 = true;
            return true;
        }

        public EnhancedClientSession(
            TcpClient client,
            IPacketHeader packetStructure,
            int listenerPort = 0)
        {
            TcpClient = client;
            PacketStructure = packetStructure;
            SequenceNumber = 0;
            ListenerPort = listenerPort;
        }

        public Task SendPacketAsync(byte[] data)
            => SendPacketAsync(data, CancellationToken.None);

        public async Task SendPacketAsync(
            byte[] data,
            CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PacketFileLogger.Log("SEND", data);
                await Stream.WriteAsync(
                    data, 0, data.Length, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        internal Task SendPreparedPacketBatchAsync(Func<IReadOnlyList<byte[]>> prepare)
        {
            return SendPreparedPacketBatchCoreAsync(_sendLock, prepare, packet =>
            {
                PacketFileLogger.Log("SEND", packet);
                return Stream.WriteAsync(packet, 0, packet.Length);
            });
        }

        internal static async Task SendPreparedPacketBatchCoreAsync(
            SemaphoreSlim sendLock,
            Func<IReadOnlyList<byte[]>> prepare,
            Func<byte[], Task> writePacket)
        {
            ArgumentNullException.ThrowIfNull(sendLock);
            ArgumentNullException.ThrowIfNull(prepare);
            ArgumentNullException.ThrowIfNull(writePacket);
            await sendLock.WaitAsync();
            try
            {
                var packets = prepare()
                    ?? throw new InvalidOperationException("Packet batch preparation returned null");
                foreach (var packet in packets)
                    await writePacket(packet);
            }
            finally
            {
                sendLock.Release();
            }
        }

        internal async Task<bool> TrySendPacketAsync(
            byte[] data,
            CancellationToken cancellationToken,
            Func<bool> canSend,
            Action onSent = null)
        {
            try
            {
                await _sendLock.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                if (cancellationToken.IsCancellationRequested
                    || (canSend != null && !canSend()))
                {
                    return false;
                }

                // The condition is rechecked while this session's send lock is
                // held. Once accepted, finish the small protocol frame without
                // cancellation so a timeout cannot create a partial packet.
                // Any newer projection/cleanup packet queues behind this write.
                PacketFileLogger.Log("SEND", data);
                await Stream.WriteAsync(
                    data, 0, data.Length, CancellationToken.None);
                onSent?.Invoke();
                return true;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        internal async Task<bool> TrySendPacketBatchAsync(
            IReadOnlyList<byte[]> logicalPackets,
            byte[] wireBatch,
            CancellationToken cancellationToken,
            Func<bool> canSend)
        {
            try
            {
                await _sendLock.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                if (cancellationToken.IsCancellationRequested
                    || (canSend != null && !canSend()))
                {
                    return false;
                }

                try
                {
                    // This batch has one send-lock linearization point and one
                    // transport write. Unlike the ordinary small-frame path,
                    // its bounded best-effort contract also covers an in-flight
                    // write. A timed-out write may be partial, so retire the old
                    // transport before the caller can retry on another session.
                    await Stream.WriteAsync(
                        wireBatch,
                        0,
                        wireBatch.Length,
                        cancellationToken);
                    PacketFileLogger.LogBatchBestEffort(
                        "SEND",
                        logicalPackets);
                    return true;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    AbortTransport();
                    return false;
                }
                catch (Exception ex)
                    when (ex is IOException
                          || ex is SocketException
                          || ex is ObjectDisposedException
                          || ex is InvalidOperationException)
                {
                    AbortTransport();
                    throw;
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void AbortTransport()
        {
            try
            {
                TcpClient?.Close();
            }
            catch
            {
                // The transport is already unusable; cleanup is best effort.
            }
        }

        public void Close()
        {
            TcpClient?.Close();
            _sendLock.Dispose();
        }
    }
}
