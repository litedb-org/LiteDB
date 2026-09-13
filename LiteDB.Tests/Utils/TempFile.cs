using System;
using System.IO;

namespace LiteDB.Tests
{
    public class TempFile : IDisposable
    {
        public string Filename { get; private set; }

        public TempFile()
        {
            var path = Path.GetTempPath();
            var name = "litedb-" + Guid.NewGuid().ToString("d").Substring(0, 5) + ".db";

            this.Filename = Path.Combine(path, name);
        }

        public TempFile(string original)
        {
            var rnd = Guid.NewGuid().ToString("d").Substring(0, 5);
            var path = Path.GetTempPath();
            var name = $"litedb-{rnd}.db";
            var filename = Path.Combine(path, name);

            File.Copy(original, filename, true);

            this.Filename = filename;
        }

        #region Dispose

        public static implicit operator String(TempFile value)
        {
            return value.Filename;
        }

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~TempFile()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // free other managed objects that implement
                // IDisposable only
            }

            // check file integrity

            File.Delete(this.Filename);

            _disposed = true;
        }

        #endregion

        public long Size => new FileInfo(this.Filename).Length;

        public string ReadAsText() => File.ReadAllText(this.Filename);

        public override string ToString() => this.Filename;
    }
}