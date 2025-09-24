using System;

namespace LiteDB
{
    /// <summary>
    /// Supported metrics for vector similarity operations.
    /// </summary>
    public enum VectorDistanceMetric : byte
    {
        Euclidean = 0,
        Cosine = 1,
        DotProduct = 2
    }
}

namespace LiteDB.Engine
{
    /// <summary>
    /// Metadata persisted for a vector-aware index.
    /// </summary>
    internal sealed class VectorIndexMetadata
    {
        /// <summary>
        /// Slot index [0-255] reserved in the collection page.
        /// </summary>
        public byte Slot { get; }

        /// <summary>
        /// Number of components expected in vector payloads.
        /// </summary>
        public ushort Dimensions { get; }

        /// <summary>
        /// Distance metric applied during nearest-neighbour evaluation.
        /// </summary>
        public VectorDistanceMetric Metric { get; }

        /// <summary>
        /// Head pointer to the persisted vector index structure.
        /// </summary>
        public PageAddress Root { get; set; }

        /// <summary>
        /// Additional metadata for engine specific bookkeeping.
        /// </summary>
        public uint Reserved { get; set; }

        public VectorIndexMetadata(byte slot, ushort dimensions, VectorDistanceMetric metric)
        {
            if (dimensions == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be greater than zero");
            }

            this.Slot = slot;
            this.Dimensions = dimensions;
            this.Metric = metric;
            this.Root = PageAddress.Empty;
            this.Reserved = uint.MaxValue;
        }

        public VectorIndexMetadata(BufferReader reader)
        {
            this.Slot = reader.ReadByte();
            this.Dimensions = reader.ReadUInt16();
            this.Metric = (VectorDistanceMetric)reader.ReadByte();
            this.Root = reader.ReadPageAddress();
            this.Reserved = reader.ReadUInt32();
        }

        public void UpdateBuffer(BufferWriter writer)
        {
            writer.Write(this.Slot);
            writer.Write(this.Dimensions);
            writer.Write((byte)this.Metric);
            writer.Write(this.Root);
            writer.Write(this.Reserved);
        }

        public static int GetLength()
        {
            return
                1 + // Slot
                2 + // Dimensions
                1 + // Metric
                PageAddress.SIZE + // Root
                4; // Reserved
        }
    }

}
