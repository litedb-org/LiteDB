using System;
using System.IO;
using System.Runtime.InteropServices;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>Private scratch storage reclaimed by the OS after abnormal exit.</summary>
    internal sealed class SortStreamFactory : IStreamFactory
    {
        private readonly Lazy<StreamFactory> _factory;
        private readonly string _filename;
        private readonly object _gate = new object();
        private bool _disposed;

        internal SortStreamFactory(string filename, string password)
        {
            _filename = filename;
            _factory = new Lazy<StreamFactory>(() =>
            {
                var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var options = FileOptions.RandomAccess | (windows ? FileOptions.DeleteOnClose : FileOptions.None);
                var stream = new FileStream(filename, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, PAGE_SIZE, options);
                try
                {
                    // Unix DeleteOnClose is implemented by managed disposal. Unlink
                    // now, retaining the open descriptor, so process death also
                    // reclaims the scratch file. Windows provides this in-kernel.
                    if (!windows) File.Delete(filename);
                    return new StreamFactory(stream, password, true);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            });
        }

        public string Name => Path.GetFileName(_filename);
        public Stream GetStream(bool canWrite, bool sequential)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SortStreamFactory));
                return _factory.Value.GetStream(canWrite, sequential);
            }
        }
        public long GetLength() => _factory.IsValueCreated ? _factory.Value.GetLength() : 0;
        public bool Exists() => _factory.IsValueCreated;
        public bool IsLocked() => false;
        public bool CloseOnDispose => true;
        public void TrimCapacity(Stream stream) { }
        public void Delete() => this.Dispose();
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_factory.IsValueCreated) _factory.Value.Dispose();
            }
        }
    }
}
