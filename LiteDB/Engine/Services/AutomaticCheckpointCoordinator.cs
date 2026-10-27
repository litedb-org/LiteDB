using System;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Engine
{
    internal enum AutomaticCheckpointState { Idle, Queued, Running, Paused, Closing, Faulted, Stopped }

    /// <summary>
    /// Opt-in Direct-core worker. Its owner must stop/join before retiring services.
    /// Request/Pause/Dispose run outside storage locks; tail/unpin notifications never do I/O.
    /// </summary>
    internal sealed class AutomaticCheckpointCoordinator : IDisposable
    {
        private static long _nextIncarnation;
        private readonly object _sync = new object();
        private readonly DiskService _disk;
        private readonly WalIndexService _wal;
        private readonly EngineState _engineState;
        private readonly CheckpointBackoff _backoff = new CheckpointBackoff();
        private Thread _thread;
        private int _checkpoint, _pauses;
        private long _logicalBytes, _epoch;
        private bool _pending, _running, _closing, _exited, _high;
        private Exception _failure;
        internal long Incarnation { get; } = Interlocked.Increment(ref _nextIncarnation);

#if DEBUG || TESTING
        internal Action BeforeCheckpointWorkerStart;
        internal Action BeforeCheckpointWorkerPass;
        internal Action AfterCheckpointWorkerPass;
        internal Action BeforeCheckpointWorkerPauseWait;
#if TESTING
        internal CheckpointBackoff Backoff => _backoff;
#endif
#endif

        internal AutomaticCheckpointCoordinator(DiskService disk, WalIndexService wal, EngineState state, int checkpoint)
        {
            _disk = disk;
            _wal = wal;
            _engineState = state;
            _checkpoint = checkpoint;
            _logicalBytes = disk.GetFileLength(FileOrigin.Log);
            _high = checkpoint > 0 && _logicalBytes >= 4L * checkpoint * Constants.PAGE_SIZE;
        }

        internal AutomaticCheckpointState State
        {
            get
            {
                lock (_sync)
                {
                    if (_failure != null) return AutomaticCheckpointState.Faulted;
                    if (_exited) return AutomaticCheckpointState.Stopped;
                    if (_closing) return AutomaticCheckpointState.Closing;
                    if (_running) return AutomaticCheckpointState.Running;
                    if (_pauses > 0) return AutomaticCheckpointState.Paused;
                    return _pending ? AutomaticCheckpointState.Queued : AutomaticCheckpointState.Idle;
                }
            }
        }

        internal bool HasExited { get { lock (_sync) return _exited; } }
        internal bool IsWorkerThread => ReferenceEquals(Thread.CurrentThread, Volatile.Read(ref _thread));

        internal long RequestEpoch { get { lock (_sync) return _epoch; } }

        internal void Request(long incarnation)
        {
            Exception failure = null;
            lock (_sync)
            {
                if (_closing || _failure != null || incarnation != this.Incarnation || _checkpoint == 0) return;
                this.Queue();
                if (_thread == null)
                {
                    try
                    {
#if DEBUG || TESTING
                        Reachability.FaultPoint("BeforeCheckpointWorkerStart");
                        BeforeCheckpointWorkerStart?.Invoke();
#endif
                        _thread = new Thread(this.Run) { IsBackground = true, Name = "LiteDB checkpoint" };
                        // An ambient request can contain AsyncLocal facade roots.
                        // The process-owned worker must retain only core services.
                        if (ExecutionContext.IsFlowSuppressed()) _thread.Start();
                        else
                        {
                            using (ExecutionContext.SuppressFlow()) _thread.Start();
                        }
                    }
                    catch (Exception error)
                    {
                        _thread = null;
                        this.Fail(error);
                        failure = error;
                    }
                }
            }
            if (failure != null) { this.CompleteFailure(failure); throw failure; }
        }

        // State-only notification, safe under WAL serialization. First thread start
        // belongs to Request, after transaction release or successful cold open.
        internal void PublishTail(long bytes)
        {
            lock (_sync)
            {
                _logicalBytes = bytes;
                if (_checkpoint == 0 || bytes <= (long)_checkpoint * Constants.PAGE_SIZE) _high = false;
                else if (!_high && bytes >= 4L * _checkpoint * Constants.PAGE_SIZE)
                {
                    _high = true;
                    _backoff.Reset();
                    if (!_closing && _failure == null) this.Queue();
                }
                Monitor.PulseAll(_sync);
            }
        }

        internal void Configure(int checkpoint, long bytes)
        {
            lock (_sync)
            {
                if (_checkpoint == checkpoint) return;
                _checkpoint = checkpoint;
                _logicalBytes = bytes;
                _high = checkpoint > 0 && bytes >= 4L * checkpoint * Constants.PAGE_SIZE;
                _backoff.Reset();
                if (checkpoint == 0) _pending = false;
                else if (!_closing && _failure == null && bytes > (long)checkpoint * Constants.PAGE_SIZE) this.Queue();
                Monitor.PulseAll(_sync);
            }
        }

        internal void ReadersChanged()
        {
            lock (_sync)
            {
                if (_closing || _failure != null || _checkpoint == 0 ||
                    _logicalBytes <= (long)_checkpoint * Constants.PAGE_SIZE) return;
                _backoff.Reset();
                this.Queue();
            }
        }

        private void Queue()
        {
            _epoch++;
            _pending = true;
            Monitor.PulseAll(_sync);
        }

        internal IDisposable Pause()
        {
            _wal.ValidateCheckpointJoin();
            lock (_sync)
            {
                if (Thread.CurrentThread == _thread) throw new InvalidOperationException("The checkpoint worker cannot join itself.");
                if (_failure != null) throw _failure;
                if (_closing) throw LiteException.EngineDisposed();
                _pauses++;
                try
                {
                    while (_running && _failure == null)
                    {
#if DEBUG || TESTING
                        Reachability.FaultPoint("BeforeCheckpointWorkerPauseWait");
                        BeforeCheckpointWorkerPauseWait?.Invoke();
#endif
                        Monitor.Wait(_sync);
                    }
                    if (_failure != null) throw _failure;
                    if (_closing) throw LiteException.EngineDisposed();
                    return new PauseLease(this);
                }
                catch { _pauses--; throw; }
            }
        }

        private void Resume()
        {
            lock (_sync) { _pauses--; Monitor.PulseAll(_sync); }
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    lock (_sync)
                    {
                        while (true)
                        {
                            if (_closing || _failure != null) return;
                            var delay = _backoff.RemainingMilliseconds;
                            if (_pending && _pauses == 0 && delay == 0) break;
                            Monitor.Wait(_sync, _pending && _pauses == 0 ? delay : Timeout.Infinite);
                        }
                        _pending = false;
                        _running = true;
                    }
#if DEBUG || TESTING
                    Reachability.FaultPoint("BeforeCheckpointWorkerPass");
                    BeforeCheckpointWorkerPass?.Invoke();
#endif
                    _wal.TryAutoCheckpoint();
#if DEBUG || TESTING
                    Reachability.FaultPoint("AfterCheckpointWorkerPass");
                    AfterCheckpointWorkerPass?.Invoke();
#endif
                    lock (_sync)
                    {
                        _running = false;
                        _logicalBytes = _disk.GetFileLength(FileOrigin.Log);
                        if (_closing || _failure != null) _pending = false;
                        else if (_checkpoint > 0 && _logicalBytes > (long)_checkpoint * Constants.PAGE_SIZE)
                        {
                            // Fixed retry deadline: requests never push it into the future.
                            _pending = true;
                            _backoff.TryClaimWaitingAttempt();
                        }
                        else _backoff.Reset();
                        Monitor.PulseAll(_sync);
                    }
                }
            }
            catch (Exception error)
            {
                lock (_sync) this.Fail(error);
                this.CompleteFailure(error); // checkpoint has unwound all storage locks
            }
            finally
            {
                lock (_sync)
                {
                    _running = false;
                    _exited = true;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        internal void Stop(Exception error)
        {
            lock (_sync) this.Fail(error);
        }

        private void CompleteFailure(Exception error)
        {
            try { _engineState.Stop(error); }
            catch (Exception cleanup)
            {
                error.Data["LiteDB.BackgroundCheckpointCleanupError"] = cleanup;
            }
        }

        private void Fail(Exception error)
        {
            _failure = _failure ?? error;
            _pending = false;
            _disk.WriteAdmission?.Close(_failure);
            Monitor.PulseAll(_sync);
        }

        internal void BeginClose()
        {
            lock (_sync)
            {
                _closing = true;
                _pending = false;
                if (_thread == null) _exited = true;
                Monitor.PulseAll(_sync);
            }
        }

        public void Dispose()
        {
            this.BeginClose();
            var thread = Volatile.Read(ref _thread);
            if (thread != Thread.CurrentThread) thread?.Join();
            lock (_sync) { if (_failure != null) throw _failure; }
        }

        private sealed class PauseLease : IDisposable
        {
            private AutomaticCheckpointCoordinator _owner;
            internal PauseLease(AutomaticCheckpointCoordinator owner) { _owner = owner; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Resume();
        }
    }
}
