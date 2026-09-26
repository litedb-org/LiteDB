using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private void OpenEngine(bool recoveredAbandonedOwner, bool final = false)
        {
#if NET8_0_OR_GREATER
            this.EnsureCoordination(allowCreate: !final);
            _coordination?.StructuralBegin();
#else
            SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
#endif
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
            this.CoordinationStage?.Invoke("opening");
#endif
            _engine = this.CreateEngine(recoveredAbandonedOwner);
#if NET8_0_OR_GREATER
            _coordination?.StructuralEnd(_engine.ReadVersion);
            _coordination?.Opened(_engine.ReadVersion);
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
            this.RetireCachedSnapshot();
#endif
        }

        private void DisposeCoordination()
        {
#if NET8_0_OR_GREATER
            lock (_snapshotGate)
            {
                _coordination?.Dispose();
                _coordination = null;
                _settings.CoordinationSignals = null;
            }
#endif
            if (_owner.TryEnter(out _, scoped: this.CanScope))
            {
                try { SharedCoordinationFallback.TryRetire(_settings.Filename); }
                finally { _owner.Exit(); }
                _owner.WaitForRelease();
            }
        }
    }
}
