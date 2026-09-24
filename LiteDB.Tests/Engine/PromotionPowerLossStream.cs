using System;
using System.IO;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    /// <summary>A device whose volatile writes disappear on power loss; flush defines durability.</summary>
    internal sealed class PromotionPowerLossStream : MemoryStream, IDurableStream
    {
        private byte[] _durable = new byte[0];
        private bool _powered = true;

        internal PromotionPowerLossStream() { }

        internal PromotionPowerLossStream(byte[] durable, byte[] cached = null)
        {
            var bytes = cached ?? durable;
            base.Write(bytes, 0, bytes.Length);
            Position = 0;
            _durable = (byte[])durable.Clone();
        }

        internal byte[] DurableBytes => (byte[])_durable.Clone();

        public void FlushToDisk()
        {
            EnsurePower();
            _durable = ToArray();
        }

        internal void PowerCut() => _powered = false;

        internal void TearWrite(byte[] buffer, int offset, int count, int prefix, bool damage, bool shortFile = false)
        {
            // A partial device write can reach durable media before the requested flush.
            var start = checked((int)Position);
            var end = start + (shortFile ? Math.Min(prefix, count) : count);
            if (_durable.Length < end) Array.Resize(ref _durable, end);
            Buffer.BlockCopy(buffer, offset, _durable, start, Math.Min(prefix, count));
            if (damage && !shortFile) Array.Clear(_durable, start + Math.Min(prefix, count), count - Math.Min(prefix, count));
            PowerCut();
        }

        internal Action<byte[], int, int> TearNextWrite;
        internal int WriteCount;

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsurePower();
            WriteCount++;
            TearNextWrite?.Invoke(buffer, offset, count);
            EnsurePower();
            base.Write(buffer, offset, count);
        }

        public override void Flush() => EnsurePower();

        public override void SetLength(long value)
        {
            EnsurePower();
            base.SetLength(value);
        }

        private void EnsurePower()
        {
            if (!_powered) throw new IOException("Modeled power loss");
        }
    }
}
