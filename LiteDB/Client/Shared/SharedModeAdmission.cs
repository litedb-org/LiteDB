using System;
using System.Runtime.ConstrainedExecution;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Lazily admits one Shared connection. Operation engines and escaping snapshots
    /// retain the same OS lease; the last owner releases it after connection disposal.
    /// </summary>
    internal sealed class SharedModeAdmission : CriticalFinalizerObject, IDisposable
    {
        private readonly EngineSettings _settings;
        private readonly object _gate = new object();
        private SharedModeGuard _guard;
        private int _references = 1;
        private bool _disposed;

        internal SharedModeAdmission(EngineSettings settings) { _settings = settings; }

        // Called under the database mutex, before coordination can revoke a peer.
        internal void Ensure()
        {
            lock (_gate) this.EnsureLocked();
        }

        private void EnsureLocked()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SharedModeAdmission));
            // Failed opens remain retryable; only successful admission is cached.
            _guard ??= SharedModeGuard.Open(_settings);
            _guard?.EnsureValid();
        }

        internal IDisposable Retain()
        {
            lock (_gate)
            {
                this.EnsureLocked();
                if (_guard == null) return null;
                var lease = new Lease(this);
                _references++;
                return lease;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                this.Release();
            }
            GC.SuppressFinalize(this);
        }

        ~SharedModeAdmission() { this.Dispose(); }

        private void Release()
        {
            lock (_gate)
            {
                if (--_references != 0) return;
                _guard?.Dispose();
                _guard = null;
            }
        }

        private sealed class Lease : CriticalFinalizerObject, IDisposable
        {
            private SharedModeAdmission _owner;
            internal Lease(SharedModeAdmission owner) { _owner = owner; }
            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release();
                GC.SuppressFinalize(this);
            }
            ~Lease() { this.Dispose(); }
        }
    }
}
