using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Internal class that implement same idea from ArraySegment[byte] but use a class (not a struct). Works for byte[] only
    /// </summary>
    internal class BufferSlice
    {
        public int Offset { get; }
        public int Count { get; }
        public byte[] Array { get; }

#if DEBUG || TESTING
        private PageBuffer _ownerFrame;
        private long _ownerGeneration;
        private Snapshot _ownerSnapshot;
        private int _ownerSnapshotEpoch;
#endif

        public BufferSlice(byte[] array, int offset, int count)
        {
            this.Array = array;
            this.Offset = offset;
            this.Count = count;
        }

        public byte this[int index]
        {
            get
            {
                this.EnsureReadable();
                return this.Array[this.Offset + index];
            }
            set
            {
                this.EnsureWritable();
                this.Array[this.Offset + index] = value;
            }
        }

        /// <summary>
        /// Clear all page content byte array (not controls)
        /// </summary>
        public void Clear()
        {
            this.EnsureWritable();
            System.Array.Clear(this.Array, this.Offset, this.Count);
        }

        /// <summary>
        /// Clear page content byte array
        /// </summary>
        public void Clear(int offset, int count)
        {
            this.EnsureWritable();
            ENSURE(offset + count <= this.Count, "must fit in this page");

            System.Array.Clear(this.Array, this.Offset + offset, count);
        }

        /// <summary>
        /// Fill all content with value. Used for DEBUG propost
        /// </summary>
        public void Fill(byte value)
        {
            this.EnsureWritable();
            for (var i = 0; i < this.Count; i++)
            {
                this.Array[this.Offset + i] = value;
            }
        }

        /// <summary>
        /// Checks if all values contains only value parameter (used for DEBUG)
        /// </summary>
        public bool All(byte value)
        {
            this.EnsureReadable();
            for (var i = 0; i < this.Count; i++)
            {
                if (this.Array[this.Offset + i] != value) return false;
            }

            return true;
        }

        /// <summary>
        /// Return byte[] slice into hex digits
        /// </summary>
        public string ToHex()
        {
            this.EnsureReadable();
            var output = new StringBuilder();
            var position = 0L;

            while(position < this.Count)
            {
                //output.Append(position.ToString("X3") + "  ");

                for (var i = 0; i < 32 && position < this.Count; i++)
                {
                    output.Append(this.Array[this.Offset + position].ToString("X2") + " ");

                    position++;
                }

                output.AppendLine();

            }

            return output.ToString();
        }

        /// <summary>
        /// Slice this buffer into new BufferSlice according new offset and new count
        /// </summary>
        public BufferSlice Slice(int offset, int count)
        {
            var slice = new BufferSlice(this.Array, this.Offset + offset, count);

#if DEBUG || TESTING
            slice._ownerFrame = _ownerFrame;
            slice._ownerGeneration = _ownerGeneration;
            slice._ownerSnapshot = _ownerSnapshot;
            slice._ownerSnapshotEpoch = _ownerSnapshotEpoch;
#endif

            return slice;
        }

        /// <summary>
        /// Convert this buffer slice into new byte[]
        /// </summary>
        public byte[] ToArray()
        {
            this.EnsureReadable();
            var buffer = new byte[this.Count];

            Buffer.BlockCopy(this.Array, this.Offset, buffer, 0, this.Count);

            return buffer;
        }

        internal void AttachOwner(PageBuffer frame, Snapshot snapshot = null, int snapshotEpoch = 0)
        {
#if DEBUG || TESTING
            _ownerFrame = frame;
            _ownerGeneration = frame?.Generation ?? 0;
            _ownerSnapshot = snapshot;
            _ownerSnapshotEpoch = snapshotEpoch;
#endif
        }

        internal void RefreshOwnerGeneration()
        {
#if DEBUG || TESTING
            _ownerGeneration = _ownerFrame?.Generation ?? 0;
            _ownerSnapshot = null;
            _ownerSnapshotEpoch = 0;
#endif
        }

        internal void EnsureReadable()
        {
#if DEBUG || TESTING
            if (_ownerFrame == null) return;

            ENSURE(_ownerFrame.Generation == _ownerGeneration, "buffer slice belongs to a recycled cache frame");
            ENSURE(_ownerSnapshot == null || _ownerSnapshot.Epoch == _ownerSnapshotEpoch, "buffer slice belongs to a cleared snapshot");
#endif
        }

        internal void EnsureWritable()
        {
#if DEBUG || TESTING
            this.EnsureReadable();
            ENSURE(_ownerFrame == null || _ownerFrame.Cache == null ||
                _ownerFrame.State == FrameState.Writable || _ownerFrame.State == FrameState.Loading,
                "buffer slice is not owned by a writable cache frame");
#endif
        }

        public override string ToString()
        {
            return $"Offset: {this.Offset} - Count: {this.Count}";
        }
    }
}
