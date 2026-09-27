using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private void OpenEngine(bool recoveredAbandonedOwner, bool final = false)
        {
            LiteDB.Engine.RebuildRecovery.EnsureAvailable(_settings);
#if NET8_0_OR_GREATER
            this.EnsureCoordination(allowCreate: !final, writing: true);
            // A valid open only reconstructs local state. Actual recovery/migration
            // mutations announce their own structural regions before touching storage.
            var recovering = _coordination?.BeginOpenRecovery() ?? false;
#else
            SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
#endif
            try
            {
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
                this.CoordinationStage?.Invoke("opening");
#endif
                _engine = this.CreateEngine(recoveredAbandonedOwner);
            }
            catch
            {
#if NET8_0_OR_GREATER
                if (recovering) _coordination.StructuralEnd(-1);
#endif
                throw;
            }
#if NET8_0_OR_GREATER
            _coordination?.Opened(_engine.ReadVersion, endStructural: recovering);
#endif
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
