using System;
using System.Text;

namespace LiteDB.Engine
{
    internal sealed class BorrowedFieldPath
    {
        private BorrowedFieldPath(string[] segments, byte[][] encodedSegments, int slot)
        {
            this.Segments = segments;
            this.EncodedSegments = encodedSegments;
            this.Slot = slot;
        }

        public string[] Segments { get; }

        public byte[][] EncodedSegments { get; }

        public int Slot { get; }

        public static bool TryCreate(string[] segments, int slot, out BorrowedFieldPath path)
        {
            var encoded = new byte[segments.Length][];

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                if (string.IsNullOrEmpty(segment))
                {
                    path = null;
                    return false;
                }

                for (var j = 0; j < segment.Length; j++)
                {
                    if (segment[j] > 0x7F)
                    {
                        // UTF-8 case folding is not equivalent to OrdinalIgnoreCase.
                        // Keep Unicode field names on the owning evaluator.
                        path = null;
                        return false;
                    }
                }

                encoded[i] = Encoding.UTF8.GetBytes(segment);
            }

            path = new BorrowedFieldPath(segments, encoded, slot);
            return true;
        }

        public bool HasSameSegments(string[] other)
        {
            if (other.Length != this.Segments.Length) return false;

            for (var i = 0; i < other.Length; i++)
            {
                if (!string.Equals(other[i], this.Segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
