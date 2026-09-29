using System;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB.Engine
{
    /// <summary>
    /// Serializes file admission with replacement, including the interval without
    /// a canonical file. Ownership is thread-scoped and abandoned on process death.
    /// The durable rebuild marker independently guards an incomplete installation.
    /// </summary>
    internal sealed class RebuildAdmission : IDisposable
    {
        private Mutex _mutex;

        private RebuildAdmission(Mutex mutex) => _mutex = mutex;

        /// <summary>
        /// Ordinary Direct opens retain support for runtimes without named mutexes.
        /// Replacement-capable and shared opens still require admission.
        /// </summary>
        internal static RebuildAdmission EnterForOpen(EngineSettings settings)
        {
            try { return Enter(settings); }
            catch (PlatformNotSupportedException ex) when (settings.SharedReaderVersions == null &&
                !settings.AutoRebuild && !settings.Upgrade &&
                !(ex.InnerException is System.ComponentModel.Win32Exception))
            {
                // SharedMutexFactory also wraps Windows access failures in this
                // exception. Those are not evidence that mutexes are unsupported.
                return null;
            }
        }

        internal static RebuildAdmission Enter(EngineSettings settings, int timeoutMilliseconds = 60000)
        {
            if (settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) ||
                settings.Filename == ":memory:" || settings.Filename == ":temp:") return null;

            var name = "litedb-rebuild-" + SharedMutexNameFactory.CreateUsingSha1(settings.Filename);
#if DEBUG || TESTING
            var mutex = settings.CreateRebuildMutex != null ? settings.CreateRebuildMutex(name) :
                SharedMutexFactory.Create(name);
#else
            var mutex = SharedMutexFactory.Create(name);
#endif
            try
            {
                try
                {
#if DEBUG || TESTING
                    settings.BeforeOpeningAdmission?.Invoke();
#endif
                    if (!mutex.WaitOne(timeoutMilliseconds))
                        throw new IOException("Timed out waiting for database opening or rebuild ownership: " + settings.Filename);
                }
                catch (AbandonedMutexException) { } // The marker, not abandonment, decides whether opening is safe.
                return new RebuildAdmission(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
