using System;
using LiteDB.Client.Shared;
using LiteDB.Utils;

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

        [TeardownPath("SharedEngine.OpenEngine", TeardownDisposition.SuppressedPreservingPrimary,
            "A core whose publication failed is closed best effort (failures swallowed) and the original error rethrown " +
            "(SharedEngine.Coordination.cs; docs/shared-teardown-callbacks.md).")]
        private void OpenEngine(bool recoveredAbandonedOwner, bool final = false, bool writing = false)
        {
            LiteDB.Engine.RebuildRecovery.EnsureAvailable(_settings);
#if NET8_0_OR_GREATER
            this.EnsureCoordination(allowCreate: !final, writing: true);
            // A fresh connection can attach only under ownership. Announce before
            // replay/open so cached peers yield during this writer's expensive work.
            if (writing) this.StartWriterPressure();
            // A valid open only reconstructs local state. Actual recovery/migration
            // mutations announce their own structural regions before touching storage.
            var recovering = _coordination?.BeginOpenRecovery() ?? false;
#else
            SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
#endif
            LiteDB.Engine.LiteEngine opened = null;
            try
            {
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
                this.CoordinationStage?.Invoke("opening");
#endif
                opened = this.CreateEngine(recoveredAbandonedOwner);
                SharedOwnershipEvents.Core(this, opened, SharedOwnershipEvents.Opened);
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
                this.CoordinationStage?.Invoke("opened");
#endif
#if NET8_0_OR_GREATER
                _coordination?.Opened(opened.ReadVersion, endStructural: recovering);
#endif
                _engine = opened;
            }
            catch
            {
                // Opening is not complete until publication succeeds. Keep failed
                // engines out of connection state and preserve the original error.
                try { if (opened != null) this.CloseRetainedCore(opened, checkpoint: false); }
                catch (Exception) { /* Best effort after a failed open; no checkpoint. */ }
#if NET8_0_OR_GREATER
                try { if (recovering) _coordination.StructuralEnd(-1); }
                catch (Exception) { /* A damaged authority remains untrusted. */ }
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
