using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement a temporary stream that uses MemoryStream until get LIMIT bytes, then copy all to tempoary disk file and delete on dispose
    /// Can be pass 
    /// </summary>
    public class TempStream : Stream
    {
        private Stream _stream = new MemoryStream();
        private string _filename = null;
        private readonly long _maxMemoryUsage;

        public TempStream(string filename = null, long maxMemoryUsage = 10485760 /* 10MB */)
        {
            _maxMemoryUsage = maxMemoryUsage;
            _filename = filename;
        }

        /// <summary>
        /// Indicate that stream are all in memory
        /// </summary>
        public bool InMemory => _stream is MemoryStream;

        /// <summary>
        /// Indicate that stream is now on this
        /// </summary>
        public bool InDisk => _stream is FileStream;

        /// <summary>
        /// Get temp disk filename (if null will be generate only when create file)
        /// </summary>
        public string Filename => _filename;

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanWrite;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => _stream.Length;

        public override long Position { get => _stream.Position; set => this.Seek(value, SeekOrigin.Begin); }

        public override void Flush() => _stream.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

        public override void SetLength(long value) => _stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

        internal void TrimCapacity()
        {
            if (_stream is MemoryStream memory)
            {
                memory.Capacity = checked((int)memory.Length);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position =
                origin == SeekOrigin.Begin ? offset :
                origin == SeekOrigin.Current ? _stream.Position + offset :
                _stream.Position - offset;

            // when offset pass limit first time, change current _stream from MemoryStrem to FileStream with TempFilename()
            if (position > _maxMemoryUsage && this.InMemory)
            {
                // create new filename if not passed on ctor (must be unique
                _filename = _filename ?? Path.Combine(Path.GetTempPath(), "litedb_" + Guid.NewGuid() + ".db");

                var file = new FileStream(_filename, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, PAGE_SIZE, FileOptions.RandomAccess);

                // copy data from memory to disk
                _stream.Position = 0;
                _stream.CopyTo(file);

                // dispose MemoryStream
                _stream.Dispose();

                // and replace with FileStream
                _stream = file;
            }

            return _stream.Seek(offset, origin);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _stream.Dispose();

            // if any file was created, let's delete now
            if (this.InDisk)
            {
                File.Delete(_filename);
            }
        }
    }
}
