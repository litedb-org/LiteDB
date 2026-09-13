using System.IO;

namespace LiteDB.Tests
{
    internal sealed class FaultingMemoryStream : MemoryStream
    {
        private readonly bool _canRead;
        private readonly bool _canWrite;
        private readonly bool _canSeek;
        private readonly bool _throwOnWrite;
        private readonly bool _throwOnFlush;
        private int _readsBeforeThrow;

        public FaultingMemoryStream(
            byte[] content = null,
            bool canRead = true,
            bool canWrite = true,
            bool canSeek = true,
            int readsBeforeThrow = -1,
            bool throwOnWrite = false,
            bool throwOnFlush = false)
        {
            _canRead = canRead;
            _canWrite = canWrite;
            _canSeek = canSeek;
            _readsBeforeThrow = readsBeforeThrow;
            _throwOnWrite = throwOnWrite;
            _throwOnFlush = throwOnFlush;

            if (content != null)
            {
                base.Write(content, 0, content.Length);
                Position = 0;
            }
        }

        public bool IsDisposed { get; private set; }
        public override bool CanRead => _canRead && base.CanRead;
        public override bool CanWrite => _canWrite && base.CanWrite;
        public override bool CanSeek => _canSeek && base.CanSeek;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readsBeforeThrow == 0) throw new IOException("Injected conversion read failure.");
            if (_readsBeforeThrow > 0) _readsBeforeThrow--;

            return base.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_throwOnWrite) throw new IOException("Injected destination copy failure.");

            base.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            if (_throwOnFlush) throw new IOException("Injected destination flush failure.");

            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
