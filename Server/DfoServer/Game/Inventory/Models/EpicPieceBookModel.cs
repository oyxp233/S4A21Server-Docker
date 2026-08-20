using DfoServer.Game.SelectCharacter;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class EpicPieceBookModel
    {
        private int[] _counts = Array.Empty<int>();
        private readonly HashSet<int> _dirtyIndexes = new HashSet<int>();

        public bool IsDirty => _dirtyIndexes.Count > 0;

        public IReadOnlyCollection<int> DirtyIndexes => _dirtyIndexes;

        public void LoadFromBlob(byte[] data)
        {
            var catalogCount = EpicPieceCatalogService.Count;
            _counts = new int[catalogCount];
            if (data != null)
            {
                var count = Math.Min(catalogCount, data.Length / sizeof(int));
                for (var index = 0; index < count; index++)
                {
                    var value = BinaryPrimitives.ReadInt32LittleEndian(
                        data.AsSpan(index * sizeof(int), sizeof(int)));
                    _counts[index] = Math.Max(0, value);
                }
            }

            _dirtyIndexes.Clear();
        }

        public byte[] ToBlob()
        {
            EnsureCatalogSize();
            var data = new byte[_counts.Length * sizeof(int)];
            for (var index = 0; index < _counts.Length; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    data.AsSpan(index * sizeof(int), sizeof(int)),
                    Math.Max(0, _counts[index]));
            }

            return data;
        }

        public int GetCountByPieceId(int pieceId)
        {
            EnsureCatalogSize();
            return EpicPieceCatalogService.TryGetIndexByPieceId(pieceId, out var index)
                && index >= 0
                && index < _counts.Length
                    ? _counts[index]
                    : 0;
        }

        public bool TryAddByPieceId(int pieceId, int count, out int finalCount)
        {
            finalCount = 0;
            if (count <= 0 || !EpicPieceCatalogService.TryGetIndexByPieceId(pieceId, out var index))
                return false;

            EnsureCatalogSize();
            var next = (long)_counts[index] + count;
            _counts[index] = next > int.MaxValue ? int.MaxValue : (int)next;
            finalCount = _counts[index];
            _dirtyIndexes.Add(index);
            return true;
        }

        public bool TryConsumeByPieceId(int pieceId, int count, out int finalCount)
        {
            finalCount = 0;
            if (count < 0 || !EpicPieceCatalogService.TryGetIndexByPieceId(pieceId, out var index))
                return false;
            if (count == 0)
            {
                finalCount = GetCountByPieceId(pieceId);
                return true;
            }

            EnsureCatalogSize();
            if (_counts[index] < count)
                return false;

            _counts[index] -= count;
            finalCount = _counts[index];
            _dirtyIndexes.Add(index);
            return true;
        }

        public bool TrySetCountByPieceId(int pieceId, int count)
        {
            if (!EpicPieceCatalogService.TryGetIndexByPieceId(pieceId, out var index))
                return false;

            EnsureCatalogSize();
            _counts[index] = Math.Max(0, count);
            _dirtyIndexes.Add(index);
            return true;
        }

        public List<ItemValueEntrySnapshot> BuildEntries(bool includeZero = false)
        {
            EnsureCatalogSize();
            var result = new List<ItemValueEntrySnapshot>();
            var entries = EpicPieceCatalogService.Entries;
            for (var index = 0; index < entries.Count && index < _counts.Length; index++)
            {
                var count = Math.Max(0, _counts[index]);
                if (!includeZero && count <= 0)
                    continue;

                result.Add(new ItemValueEntrySnapshot
                {
                    ItemId = entries[index].EpicPieceId,
                    Value = count,
                });
            }

            return result;
        }

        public ItemValueEntrySnapshot BuildEntryByPieceId(int pieceId)
        {
            return new ItemValueEntrySnapshot
            {
                ItemId = pieceId,
                Value = GetCountByPieceId(pieceId),
            };
        }

        public void CopyFrom(EpicPieceBookModel source)
        {
            LoadFromBlob(source != null ? source.ToBlob() : null);
            ClearDirtyState();
        }

        public void ClearDirtyState()
        {
            _dirtyIndexes.Clear();
        }

        private void EnsureCatalogSize()
        {
            var catalogCount = EpicPieceCatalogService.Count;
            if (_counts.Length == catalogCount)
                return;

            Array.Resize(ref _counts, catalogCount);
        }
    }
}
