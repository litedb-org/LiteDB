using System;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexNode
    {
        private const int P_DATA_BLOCK = 0;
        private const int P_NEXT_NODE = P_DATA_BLOCK + PageAddress.SIZE;
        private const int P_VECTOR = P_NEXT_NODE + PageAddress.SIZE;

        private readonly VectorIndexPage _page;
        private readonly BufferSlice _segment;

        public PageAddress Position { get; }

        public PageAddress DataBlock { get; private set; }

        public PageAddress Next { get; private set; }

        public VectorIndexPage Page => _page;

        public int Dimensions { get; }

        public VectorIndexNode(VectorIndexPage page, byte index, BufferSlice segment)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.DataBlock = segment.ReadPageAddress(P_DATA_BLOCK);
            this.Next = segment.ReadPageAddress(P_NEXT_NODE);

            var vector = segment.ReadVector(P_VECTOR);
            this.Dimensions = vector.Length;
        }

        public VectorIndexNode(VectorIndexPage page, byte index, BufferSlice segment, PageAddress dataBlock, PageAddress next, float[] vector)
        {
            _page = page;
            _segment = segment;

            this.Position = new PageAddress(page.PageID, index);
            this.DataBlock = dataBlock;
            this.Next = next;
            this.Dimensions = vector.Length;

            segment.Write(dataBlock, P_DATA_BLOCK);
            segment.Write(next, P_NEXT_NODE);
            segment.Write(vector, P_VECTOR);

            page.IsDirty = true;
        }

        public static int GetLength(int dimensions)
        {
            return
                PageAddress.SIZE + // DataBlock
                PageAddress.SIZE + // Next
                2 + // vector length (ushort)
                (dimensions * sizeof(float));
        }

        public void SetNext(PageAddress next)
        {
            this.Next = next;
            _segment.Write(next, P_NEXT_NODE);
            _page.IsDirty = true;
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
