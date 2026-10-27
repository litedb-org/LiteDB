using System;
using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        /// <summary>
        /// Why this connection could not attach to mapped Shared reads. Null means
        /// no fallback has been recorded, not that mapped reads are active. Available
        /// on .NET 8+; other targets always use protected reads and return null.
        /// </summary>
        public string CoordinationFallbackReason { get; private set; }

        private void OpenEngine(bool recoveredAbandonedOwner, bool final = false, bool writing = false)
        {
            LiteDB.Engine.RebuildRecovery.EnsureAvailable(_settings);
            LiteDB.Engine.LiteEngine opened = null;
#if NET8_0_OR_GREATER
            var writableMapped = _settings.SharedOneFile && !_settings.ReadOnly && !_settings.ReadOnlyStorage;
            var recovering = false;
            SharedMemoryReaderTable mapped = null;
#endif
            try
            {
#if NET8_0_OR_GREATER
                if (writableMapped) this.EnterMappedWriter();
                this.EnsureCoordination(allowCreate: !final, writing: true);
                if (writing) this.StartWriterPressure();
                recovering = _coordination?.BeginOpenRecovery() ?? false;
                mapped = _settings.SharedOneFile ? _settings.CoordinationSignals as SharedMemoryReaderTable : null;
                if (mapped != null && writableMapped)
                {
                    _settings.SharedWriterTenure = mapped.WriterTenure;
                    mapped.BeginOpenRecovery();
                }
#else
                SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
#endif
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
                this.CoordinationStage?.Invoke("opening");
#endif
                opened = this.CreateEngine(recoveredAbandonedOwner);
                SharedOwnershipEvents.Core(this, opened, SharedOwnershipEvents.Opened);
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
                this.CoordinationStage?.Invoke("opened");
#endif
#if NET8_0_OR_GREATER
                if (_coordination != null) _coordination.Opened(opened.ReadVersion, endStructural: recovering);
                else if (mapped != null && writableMapped)
                {
                    opened.SyncSharedRecovery();
                    mapped.Opened(opened.ReadVersion);
                }
#endif
                _engine = opened;
            }
            catch
            {
                // Opening is not complete until publication succeeds. Keep failed
                // engines out of connection state and preserve the original error.
#if NET8_0_OR_GREATER
                try { if (writableMapped && _mappedWriterHeld) mapped?.Stopped(_settings.SharedWriterTenure); }
                catch (Exception) { /* Unavailable authority already refuses readers. */ }
#endif
                try { if (opened != null) this.CloseRetainedCore(opened, checkpoint: false); }
                catch (Exception) { /* Best effort after a failed open; no checkpoint. */ }
#if NET8_0_OR_GREATER
                try { if (recovering) _coordination.StructuralEnd(-1); }
                catch (Exception) { /* A damaged authority remains untrusted. */ }
                try { if (writableMapped) this.ExitMappedWriter(false); }
                catch (Exception) { /* Release runs in finally; retain the opening failure. */ }
#endif
                throw;
            }
#if DEBUG || TESTING
            this.EngineOpens++;
#endif
            _recoveryReport = _engine.RecoveryReport ?? _recoveryReport;
            _engine.RecoveryReport = _recoveryReport;
        }

        private void RetireCoordinatedReads()
        {
#if NET8_0_OR_GREATER
            if (_settings.SharedOneFile && System.Threading.Volatile.Read(ref _disposed) != 0) return;
            lock (_snapshotGate)
            {
                _readCacheDemand = 0;
                this.RetireCachedSnapshot();
            }
#endif
        }

        private void DisposeCoordination()
        {
#if NET8_0_OR_GREATER
            lock (_snapshotGate)
            {
                if (_coordination == null) return;
                _coordination.Dispose();
                _coordination = null;
                _settings.CoordinationSignals = null;
            }
            if (_owner.TryEnter(out _, scoped: this.CanScope))
            {
                try { SharedCoordinationFallback.TryRetire(_settings.Filename); }
                finally { _owner.Exit(); }
                _owner.WaitForRelease();
            }
#endif
        }
    }
}
