using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private bool AllowAutomaticRebuild()
        {
            _settings.CoordinationSignals?.SlotReused();
            return !_readers.OldestVersion().HasValue;
        }

        private void OpenEngine(bool recoveredAbandonedOwner, bool final = false)
        {
            LiteDB.Engine.RebuildRecovery.EnsureAvailable(_settings);
#if NET8_0_OR_GREATER
            this.EnsureCoordination(allowCreate: !final);
            this.PrepareWriterResume(recoveredAbandonedOwner);
            _coordination?.OpeningBegin();
            if (_settings.Upgrade) _coordination?.SlotReused();
#else
            SharedCoordinationFallback.RevokeIfPresent(_settings.Filename);
#endif
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
            this.CoordinationStage?.Invoke("opening");
#endif
            try { _engine = this.CreateEngine(recoveredAbandonedOwner); }
            finally { _settings.WriterResume = null; _settings.WriterResumeValid = null; }
#if NET8_0_OR_GREATER
            _coordination?.OpeningEnd(_engine.ReadVersion);
#endif
#if DEBUG || TESTING
            this.EngineOpens++;
            if (_engine.ResumedWriter) this.WriterResumeCount++;
#endif
            _recoveryReport = _engine.RecoveryReport ?? _recoveryReport;
            _engine.RecoveryReport = _recoveryReport;
        }

        private void CloseResumableWriter(LiteDB.Engine.LiteEngine engine)
        {
            engine.Close();
#if NET8_0_OR_GREATER
            _writerResume = engine.ClosedWriterResume;
            if (_writerResume != null && _coordination != null && _coordination.TryRead(out var status))
                _writerResumeStatus = status;
            else _writerResume = null;
#endif
        }

#if DEBUG || TESTING
        internal int WriterResumeCount;
#endif

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
                _writerResume = null;
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
