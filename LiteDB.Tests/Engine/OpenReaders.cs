using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Readers a test disposes at a chosen moment (often on another thread). A failure before
    /// that moment would leak them: a leased snapshot's pinned page buffers then reach the
    /// TESTING leak detector in PageBuffer's finalizer, which terminates the test host and
    /// hides the original failure. Disposing tracked readers again at test cleanup is safe
    /// because shared reader disposal is idempotent; errors are ignored so they cannot mask it.
    /// </summary>
    internal sealed class OpenReaders : IDisposable
    {
        private readonly List<IDisposable> _readers = new List<IDisposable>();

        public T Track<T>(T reader) where T : IDisposable
        {
            lock (_readers) _readers.Add(reader);
            return reader;
        }

        public void Dispose()
        {
            IDisposable[] readers;
            lock (_readers)
            {
                readers = _readers.ToArray();
                _readers.Clear();
            }
            foreach (var reader in readers)
            {
                try { reader?.Dispose(); }
                catch (Exception) { /* Cleanup after the test; its own outcome is already decided. */ }
            }
        }
    }
}
