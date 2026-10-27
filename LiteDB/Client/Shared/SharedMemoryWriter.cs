#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // Main ownership precedes this gate and WRITE. Failed publications remain
    // fenced until an independently opened, synced core repairs the authority.
    internal sealed class SharedMemoryWriter
    {
        private readonly SharedMemoryAuthority Authority;
        private readonly object _writerGate = new object();
        private Thread _writerThread;
        private int _writerDepth;
        private bool _openFence, _uncertain, _borrowedWrite;
        private int _structuralDepth;
        private long _writerMarker;
        private EngineState _state;
        internal long Tenure { get; private set; }
        internal void BindState(EngineState state, long tenure)
        {
            lock (_writerGate)
                if (_writerDepth != 0 && Tenure == tenure) _state = state;
        }
        internal void Stopped(long tenure)
        {
            lock (_writerGate)
                if (_writerDepth != 0 && Tenure == tenure) FenceUncertain();
        }
        internal SharedMemoryWriter(SharedMemoryAuthority authority) => Authority = authority;
        internal int[] Scan()
        {
            lock (_writerGate) return SharedMemoryReaderTable.Scan(Authority, _writerDepth != 0 && !_uncertain);
        }
        public void ValidateWrite()
        {
            lock (_writerGate) EnsureWriter();
        }
        public void FenceUncertain()
        {
            lock (_writerGate)
            {
                _uncertain = true;
                BeginFence();
                Authority.Map.Store(SharedMemoryLayout.RecoveryOwner, _writerMarker);
                SharedMemoryReaderTable.PublishStage("writer-uncertain");
            }
        }
        /// <summary>
        /// Claim the mapped WRITE byte for the complete lifetime of this process's
        /// writable core.  The named database mutex orders local facades; this byte
        /// makes the same tenure visible to every process and is released only after
        /// its final publication fence is complete.
        /// </summary>
        internal void Enter(bool borrowedWrite = false)
        {
            lock (_writerGate)
            {
                while (_writerDepth != 0 && !ReferenceEquals(_writerThread, Thread.CurrentThread))
                    Monitor.Wait(_writerGate);
                if (_writerDepth != 0)
                {
                    _writerDepth++;
                    return;
                }

                SharedMemoryReaderTable.RequireHeader(Authority, initializing: borrowedWrite);
                if (!borrowedWrite) Authority.Locks.EnterWrite();
                _borrowedWrite = borrowedWrite;
                try
                {
                    // Preflight checked counters before publishing partial ownership.
                    var epoch = checked(Authority.Map.Load(SharedMemoryLayout.WriterTenureEpoch) + 1);
                    var sequence = Authority.Map.Load(SharedMemoryLayout.Control + 8);
                    checked { _ = sequence + ((sequence & 1) == 0 ? 2 : 1); }
                    Tenure = epoch;
                    _writerThread = Thread.CurrentThread;
                    _writerDepth = 1;
                    _writerMarker = Marker();
                    Authority.Map.Store(SharedMemoryLayout.WriterTenureEpoch, epoch);
                    SharedMemoryReaderTable.PublishStage("writer-epoch");
                    Authority.Map.Store(SharedMemoryLayout.WriterOwner, _writerMarker);
                    SharedMemoryReaderTable.PublishStage("writer-owner");
                    Authority.Map.Store(SharedMemoryLayout.RecoveryOwner, _writerMarker);
                    SharedMemoryReaderTable.PublishStage("recovery-owner");
                    BeginFence();
                    SharedMemoryReaderTable.PublishStage("writer-begin");
                }
                catch
                {
                    try { if (_writerDepth != 0) FenceUncertain(); }
                    finally { Release(); }
                    throw;
                }
            }
        }

        internal void Exit(bool publish)
        {
            lock (_writerGate)
            {
                if (_writerDepth == 0) return;
                if (!ReferenceEquals(_writerThread, Thread.CurrentThread) && _writerDepth > 1)
                    throw new InvalidOperationException("Shared mapped writer tenure ended on a different thread.");
                if (--_writerDepth != 0) return;

                try
                {
                    if (publish && !_uncertain && !_openFence && _structuralDepth == 0)
                    {
                        SharedMemoryReaderTable.RequireHeader(Authority, _borrowedWrite);
                        ClearRecoveryOwner();
                        Authority.Map.Store(SharedMemoryLayout.WriterOwner, 0);
                        EndFence();
                        SharedMemoryReaderTable.PublishStage("writer-end");
                    }
                    else FenceUncertain();
                }
                catch
                {
                    FenceUncertain();
                    throw;
                }
                finally { Release(); }
            }
        }

        private void Release()
        {
            _writerDepth = 0;
            _writerThread = null;
            _writerMarker = 0;
            Tenure = 0;
            _state = null;
            try { if (!_borrowedWrite) Authority.Locks.ExitWrite(); }
            finally { _borrowedWrite = false; Monitor.PulseAll(_writerGate); }
            SharedMemoryReaderTable.PublishStage("write-released");
        }

        internal void BeginOpenRecovery()
        {
            lock (_writerGate)
            {
                EnsureWriter();
                _openFence = true;
                BeginFence();
                Authority.Map.Store(SharedMemoryLayout.RecoveryOwner, _writerMarker);
                SharedMemoryReaderTable.PublishStage("open-begin");
            }
        }

        internal void Opened(int version)
        {
            lock (_writerGate)
            {
                EnsureWriter();
                if (version >= 0) SetVersion(version);
                var structural = Authority.Map.Load(SharedMemoryLayout.StructuralEpoch);
                if ((structural & 1) != 0)
                    Authority.Map.Store(SharedMemoryLayout.StructuralEpoch, checked(structural + 1));
                SharedMemoryReaderTable.PublishStage("open-structural-repaired");
                _openFence = false;
                _uncertain = false;
                _structuralDepth = 0;
                ClearRecoveryOwner();
                EndFence();
                SharedMemoryReaderTable.PublishStage("open-end");
            }
        }

        private void EnsureWriter()
        {
            if (_writerDepth == 0) throw new InvalidOperationException("Mapped publication requires WRITE ownership.");
            SharedMemoryReaderTable.RequireHeader(Authority, _borrowedWrite);
            // Checkpoint publishes its stop before unwinding structural scopes,
            // and completes teardown only after their locks have been released.
            if (_state != null && (_state.Stopped || _state.WriteFailure != null))
            {
                FenceUncertain();
                _state.ThrowIfStopped();
                _state.RequireNoWriteFailure();
            }
        }

        private void BeginFence()
        {
            var sequence = Authority.Map.Load(SharedMemoryLayout.Control + 8);
            if ((sequence & 1) == 0) Authority.Map.Store(SharedMemoryLayout.Control + 8, checked(sequence + 1));
            SharedMemoryReaderTable.PublishStage("fence-begin");
        }

        private void EndFence()
        {
            var sequence = Authority.Map.Load(SharedMemoryLayout.Control + 8);
            if ((sequence & 1) != 0) Authority.Map.Store(SharedMemoryLayout.Control + 8, checked(sequence + 1));
            SharedMemoryReaderTable.PublishStage("fence-end");
        }

        private void ClearRecoveryOwner()
        {
            Authority.Map.Store(SharedMemoryLayout.RecoveryOwner, 0);
            SharedMemoryReaderTable.PublishStage("recovery-cleared");
        }

        private void SetVersion(int version)
        {
            var current = Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            if (version < current)
            {
                Authority.Map.Store(SharedMemoryLayout.ResetEpoch,
                    checked(Authority.Map.Load(SharedMemoryLayout.ResetEpoch) + 1));
                SharedMemoryReaderTable.PublishStage("version-reset");
            }
            Authority.Map.Store(SharedMemoryLayout.CommittedVersion, version);
        }

        private long Marker()
        {
            var bytes = Authority.Incarnation.ToByteArray();
            return BitConverter.ToInt64(bytes, 0) | 1;
        }

        public void StructuralBegin()
        {
            lock (_writerGate)
            {
                EnsureWriter();
                if (_structuralDepth++ != 0) return;
                BeginFence();
                var epoch = Authority.Map.Load(SharedMemoryLayout.StructuralEpoch);
                if ((epoch & 1) == 0) Authority.Map.Store(SharedMemoryLayout.StructuralEpoch, checked(epoch + 1));
                Authority.Map.Store(SharedMemoryLayout.RecoveryOwner, _writerMarker);
                SharedMemoryReaderTable.PublishStage("structural-begin");
            }
        }

        public void StructuralEnd(int version)
        {
            lock (_writerGate)
            {
                EnsureWriter();
                if (_structuralDepth <= 0) throw new InvalidOperationException("Unbalanced Shared mapped structural publication.");
                if (--_structuralDepth != 0) return;
                if (version >= 0 && !_uncertain && !_openFence) SetVersion(version);
                var epoch = Authority.Map.Load(SharedMemoryLayout.StructuralEpoch);
                if ((epoch & 1) != 0) Authority.Map.Store(SharedMemoryLayout.StructuralEpoch, checked(epoch + 1));
                if (!_openFence && !_uncertain)
                {
                    ClearRecoveryOwner();
                    EndFence();
                }
                SharedMemoryReaderTable.PublishStage("structural-end");
            }
        }

        public void SlotReused()
        {
            lock (_writerGate)
            {
                EnsureWriter();
                if (_uncertain) return;
                var ownFence = (Authority.Map.Load(SharedMemoryLayout.Control + 8) & 1) == 0;
                if (ownFence) BeginFence();
                Authority.Map.Store(SharedMemoryLayout.ReuseEpoch,
                    checked(Authority.Map.Load(SharedMemoryLayout.ReuseEpoch) + 1));
                SharedMemoryReaderTable.PublishStage("slot-reused");
                if (ownFence) EndFence();
            }
        }

        public void Committed(int version)
        {
            lock (_writerGate)
            {
                EnsureWriter();
                if (_uncertain) return;
                // A slower completion must not reset the epoch or erase a newer
                // barrier's publication. Resets belong only to recovery/checkpoint.
                if (version < Authority.Map.Load(SharedMemoryLayout.CommittedVersion)) return;
                var ownFence = (Authority.Map.Load(SharedMemoryLayout.Control + 8) & 1) == 0;
                if (ownFence) BeginFence();
                SetVersion(version);
                SharedMemoryReaderTable.PublishStage("commit-version");
                if (ownFence) EndFence();
            }
        }

    }
}
#endif
