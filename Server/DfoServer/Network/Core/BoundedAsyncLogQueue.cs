using System;
using System.Threading;
using System.Threading.Channels;

namespace DfoServer
{
    /// <summary>
    /// Fixed-capacity, non-blocking log queue. Network handlers must never
    /// wait for disk I/O or retain an unbounded number of pending messages.
    /// </summary>
    internal sealed class BoundedAsyncLogQueue
    {
        private readonly Channel<string> _channel;
        private long _droppedCount;

        public BoundedAsyncLogQueue(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            Capacity = capacity;
            _channel = Channel.CreateBounded<string>(
                new BoundedChannelOptions(capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
        }

        public int Capacity { get; }

        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        public bool TryEnqueue(string line)
        {
            if (_channel.Writer.TryWrite(line))
                return true;

            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        public ChannelReader<string> Reader => _channel.Reader;

        public bool TryComplete() => _channel.Writer.TryComplete();
    }
}
