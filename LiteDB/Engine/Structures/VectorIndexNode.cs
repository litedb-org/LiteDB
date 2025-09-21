using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexNode
    {
        private const int P_DATA_BLOCK = 0;
        private const int P_LEVEL_COUNT = P_DATA_BLOCK + PageAddress.SIZE;
        private const int P_LEVELS = P_LEVEL_COUNT + 1;

        public const int MaxLevels = 4;
        public const int MaxNeighborsPerLevel = 8;

        private const int LEVEL_STRIDE = 1 + (MaxNeighborsPerLevel * PageAddress.SIZE);
        private const int P_VECTOR = P_LEVELS + (MaxLevels * LEVEL_STRIDE);

        private readonly VectorIndexPage _page;
        private readonly BufferSlice _segment;

        public PageAddress Position { get; }

        public PageAddress DataBlock { get; private set; }

        public VectorIndexPage Page => _page;

        public byte LevelCount { get; private set; }

        public int Dimensions { get; }

        public VectorIndexNode(VectorIndexPage page, byte index, BufferSlice segment)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.DataBlock = segment.ReadPageAddress(P_DATA_BLOCK);
            this.LevelCount = segment.ReadByte(P_LEVEL_COUNT);

            var vector = segment.ReadVector(P_VECTOR);
            this.Dimensions = vector.Length;
        }

        public VectorIndexNode(VectorIndexPage page, byte index, BufferSlice segment, PageAddress dataBlock, float[] vector, byte levelCount)
        {
            if (vector == null) throw new ArgumentNullException(nameof(vector));
            if (levelCount == 0 || levelCount > MaxLevels) throw new ArgumentOutOfRangeException(nameof(levelCount));

            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.DataBlock = dataBlock;
            this.LevelCount = levelCount;
            this.Dimensions = vector.Length;

            segment.Write(dataBlock, P_DATA_BLOCK);
            segment.Write(levelCount, P_LEVEL_COUNT);

            for (var level = 0; level < MaxLevels; level++)
            {
                var offset = GetLevelOffset(level);
                segment.Write((byte)0, offset);

                var position = offset + 1;

                for (var i = 0; i < MaxNeighborsPerLevel; i++)
                {
                    segment.Write(PageAddress.Empty, position);
                    position += PageAddress.SIZE;
                }
            }

            segment.Write(vector, P_VECTOR);

            page.IsDirty = true;
        }

        public static int GetLength(int dimensions)
        {
            return
                PageAddress.SIZE + // DataBlock
                1 + // Level count
                (MaxLevels * LEVEL_STRIDE) +
                2 + // vector length prefix
                (dimensions * sizeof(float));
        }

        public IReadOnlyList<PageAddress> GetNeighbors(int level)
        {
            if (level < 0 || level >= MaxLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            var offset = GetLevelOffset(level);
            var count = _segment.ReadByte(offset);
            var neighbors = new List<PageAddress>(count);
            var position = offset + 1;

            for (var i = 0; i < count; i++)
            {
                neighbors.Add(_segment.ReadPageAddress(position));
                position += PageAddress.SIZE;
            }

            return neighbors;
        }

        public void SetNeighbors(int level, IReadOnlyList<PageAddress> neighbors)
        {
            if (level < 0 || level >= MaxLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            if (neighbors == null)
            {
                throw new ArgumentNullException(nameof(neighbors));
            }

            var offset = GetLevelOffset(level);
            var count = Math.Min(neighbors.Count, MaxNeighborsPerLevel);

            _segment.Write((byte)count, offset);

            var position = offset + 1;

            var i = 0;

            for (; i < count; i++)
            {
                _segment.Write(neighbors[i], position);
                position += PageAddress.SIZE;
            }

            for (; i < MaxNeighborsPerLevel; i++)
            {
                _segment.Write(PageAddress.Empty, position);
                position += PageAddress.SIZE;
            }

            _page.IsDirty = true;
        }

        public bool TryAddNeighbor(int level, PageAddress address)
        {
            if (level < 0 || level >= MaxLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            var current = this.GetNeighbors(level);

            if (current.Contains(address))
            {
                return false;
            }

            if (current.Count >= MaxNeighborsPerLevel)
            {
                return false;
            }

            var expanded = new List<PageAddress>(current.Count + 1);
            expanded.AddRange(current);
            expanded.Add(address);

            this.SetNeighbors(level, expanded);

            return true;
        }

        public bool RemoveNeighbor(int level, PageAddress address)
        {
            if (level < 0 || level >= MaxLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            var current = this.GetNeighbors(level);

            if (!current.Contains(address))
            {
                return false;
            }

            var reduced = current
                .Where(x => x != address)
                .ToList();

            this.SetNeighbors(level, reduced);

            return true;
        }

        public void SetLevelCount(byte levelCount)
        {
            if (levelCount == 0 || levelCount > MaxLevels)
            {
                throw new ArgumentOutOfRangeException(nameof(levelCount));
            }

            this.LevelCount = levelCount;
            _segment.Write(levelCount, P_LEVEL_COUNT);
            _page.IsDirty = true;
        }

        private static int GetLevelOffset(int level)
        {
            return P_LEVELS + (level * LEVEL_STRIDE);
        }

        public void UpdateVector(float[] vector)
        {
            if (vector == null) throw new ArgumentNullException(nameof(vector));

            if (vector.Length != this.Dimensions)
            {
                throw new ArgumentException("Vector length must match node dimensions.", nameof(vector));
            }

            _segment.Write(vector, P_VECTOR);
            _page.IsDirty = true;
        }

        public float[] ReadVector()
        {
            return _segment.ReadVector(P_VECTOR);
        }
    }
}
