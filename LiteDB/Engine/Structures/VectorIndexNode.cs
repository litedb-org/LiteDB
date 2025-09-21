using System;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexNode
    {
        private const int P_DATA_BLOCK = 0;
        private const int P_LEFT_NODE = P_DATA_BLOCK + PageAddress.SIZE;
        private const int P_RIGHT_NODE = P_LEFT_NODE + PageAddress.SIZE;
        private const int P_LEFT_MIN = P_RIGHT_NODE + PageAddress.SIZE;
        private const int P_LEFT_MAX = P_LEFT_MIN + 4;
        private const int P_RIGHT_MIN = P_LEFT_MAX + 4;
        private const int P_RIGHT_MAX = P_RIGHT_MIN + 4;
        private const int P_VECTOR = P_RIGHT_MAX + 4;

        private readonly VectorIndexPage _page;
        private readonly BufferSlice _segment;

        public PageAddress Position { get; }

        public PageAddress DataBlock { get; private set; }

        public VectorIndexPage Page => _page;

        public PageAddress Left { get; private set; }

        public PageAddress Right { get; private set; }

        public float LeftMinDistance { get; private set; }

        public float LeftMaxDistance { get; private set; }

        public float RightMinDistance { get; private set; }

        public float RightMaxDistance { get; private set; }

        public int Dimensions { get; }

        public VectorIndexNode(VectorIndexPage page, byte index, BufferSlice segment)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.DataBlock = segment.ReadPageAddress(P_DATA_BLOCK);
            this.Left = segment.ReadPageAddress(P_LEFT_NODE);
            this.Right = segment.ReadPageAddress(P_RIGHT_NODE);
            this.LeftMinDistance = segment.ReadSingle(P_LEFT_MIN);
            this.LeftMaxDistance = segment.ReadSingle(P_LEFT_MAX);
            this.RightMinDistance = segment.ReadSingle(P_RIGHT_MIN);
            this.RightMaxDistance = segment.ReadSingle(P_RIGHT_MAX);

            var vector = segment.ReadVector(P_VECTOR);
            this.Dimensions = vector.Length;
        }

        public VectorIndexNode(VectorIndexPage page, byte index, BufferSlice segment, PageAddress dataBlock, float[] vector)
        {
            if (vector == null) throw new ArgumentNullException(nameof(vector));

            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.DataBlock = dataBlock;
            this.Left = PageAddress.Empty;
            this.Right = PageAddress.Empty;
            this.LeftMinDistance = float.PositiveInfinity;
            this.LeftMaxDistance = float.NegativeInfinity;
            this.RightMinDistance = float.PositiveInfinity;
            this.RightMaxDistance = float.NegativeInfinity;
            this.Dimensions = vector.Length;

            segment.Write(dataBlock, P_DATA_BLOCK);
            segment.Write(PageAddress.Empty, P_LEFT_NODE);
            segment.Write(PageAddress.Empty, P_RIGHT_NODE);
            segment.Write(float.PositiveInfinity, P_LEFT_MIN);
            segment.Write(float.NegativeInfinity, P_LEFT_MAX);
            segment.Write(float.PositiveInfinity, P_RIGHT_MIN);
            segment.Write(float.NegativeInfinity, P_RIGHT_MAX);
            segment.Write(vector, P_VECTOR);

            page.IsDirty = true;
        }

        public static int GetLength(int dimensions)
        {
            return
                PageAddress.SIZE + // DataBlock
                PageAddress.SIZE + // Left child
                PageAddress.SIZE + // Right child
                4 + // Left min distance
                4 + // Left max distance
                4 + // Right min distance
                4 + // Right max distance
                2 + // vector length prefix
                (dimensions * sizeof(float));
        }

        public void SetLeft(PageAddress left)
        {
            this.Left = left;
            _segment.Write(left, P_LEFT_NODE);
            _page.IsDirty = true;
        }

        public void SetRight(PageAddress right)
        {
            this.Right = right;
            _segment.Write(right, P_RIGHT_NODE);
            _page.IsDirty = true;
        }

        public void SetLeftRange(float min, float max)
        {
            this.LeftMinDistance = min;
            this.LeftMaxDistance = max;
            _segment.Write(min, P_LEFT_MIN);
            _segment.Write(max, P_LEFT_MAX);
            _page.IsDirty = true;
        }

        public void SetRightRange(float min, float max)
        {
            this.RightMinDistance = min;
            this.RightMaxDistance = max;
            _segment.Write(min, P_RIGHT_MIN);
            _segment.Write(max, P_RIGHT_MAX);
            _page.IsDirty = true;
        }

        public void UpdateLeftRange(float distance)
        {
            var min = this.LeftMinDistance;
            var max = this.LeftMaxDistance;

            if (float.IsPositiveInfinity(min) || distance < min)
            {
                min = distance;
            }

            if (float.IsNegativeInfinity(max) || distance > max)
            {
                max = distance;
            }

            this.SetLeftRange(min, max);
        }

        public void UpdateRightRange(float distance)
        {
            var min = this.RightMinDistance;
            var max = this.RightMaxDistance;

            if (float.IsPositiveInfinity(min) || distance < min)
            {
                min = distance;
            }

            if (float.IsNegativeInfinity(max) || distance > max)
            {
                max = distance;
            }

            this.SetRightRange(min, max);
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

