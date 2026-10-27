using System;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Core-local writer permits. Installation is disabled until Direct lifetime integration.
    /// No storage lock may be acquired under _sync. Permits remain counted through CP0.
    /// </summary>
    internal sealed class CheckpointWriteAdmission
    {
        internal const int PermitLimit = 64;
        private readonly object _sync = new object();
        private int _checkpoint;
        private long _pages;
        private int _writers;
        private bool _pressure, _closed;
        private Exception _failure;

        internal CheckpointWriteAdmission(int checkpoint, long logicalBytes)
        {
            this.Configure(checkpoint, logicalBytes);
        }

        internal int Writers { get { lock (_sync) return _writers; } }
        internal bool UnderPressure { get { lock (_sync) return _pressure; } }

#if DEBUG || TESTING
        internal Action BeforeCheckpointWriterWait;
#endif

        internal Permit Acquire(TimeSpan timeout, bool canWait, Action validate)
        {
            var started = Stopwatch.GetTimestamp();
            lock (_sync)
            {
                while (true)
                {
                    this.ThrowIfStopped();
                    validate(); // owner/state checks only; never storage or host admission
                    if (_checkpoint == 0 || (!_pressure && _writers < PermitLimit))
                    {
                        _writers++;
                        return new Permit(this);
                    }
                    if (!canWait)
                        throw new LiteException(LiteException.LOCK_TIMEOUT,
                            "Checkpoint writer admission is unavailable. Complete the current reader or callback before retrying the write.");
                    var remaining = timeout.TotalMilliseconds -
                        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    if (remaining <= 0)
                    {
                        this.ThrowIfStopped();
                        validate();
                        throw LiteException.LockTimeout("checkpoint writer admission", timeout);
                    }
#if DEBUG || TESTING
                    LiteDB.Utils.Reachability.FaultPoint("BeforeCheckpointWriterWait");
                    BeforeCheckpointWriterWait?.Invoke();
#endif
                    Monitor.Wait(_sync, (int)Math.Min(Math.Ceiling(remaining), 100));
                }
            }
        }

        internal void Configure(int checkpoint, long logicalBytes)
        {
            if (checkpoint < 0) throw new ArgumentOutOfRangeException(nameof(checkpoint));
            lock (_sync)
            {
                var changed = _checkpoint != checkpoint;
                _checkpoint = checkpoint;
                _pages = logicalBytes / Constants.PAGE_SIZE;
                // A committed pragma recomputes the predicate, including a raised N.
                if (changed) _pressure = checkpoint > 0 && _pages >= 4L * checkpoint;
                else this.UpdatePressure();
                Monitor.PulseAll(_sync);
            }
        }

        internal void PublishTail(long logicalBytes)
        {
            lock (_sync)
            {
                _pages = logicalBytes / Constants.PAGE_SIZE;
                this.UpdatePressure();
                Monitor.PulseAll(_sync);
            }
        }

        private void UpdatePressure()
        {
            if (_checkpoint == 0 || _pages <= _checkpoint) _pressure = false;
            else if (_pages >= 4L * _checkpoint) _pressure = true;
        }

        internal void Wake() { lock (_sync) Monitor.PulseAll(_sync); }

        internal void Close(Exception failure = null)
        {
            lock (_sync)
            {
                _failure = _failure ?? failure;
                _closed = true;
                Monitor.PulseAll(_sync);
            }
        }

        private void ThrowIfStopped()
        {
            if (_failure != null) throw _failure;
            if (_closed) throw LiteException.EngineDisposed();
        }

        private void Release()
        {
            lock (_sync)
            {
                _writers--;
                Monitor.PulseAll(_sync);
            }
        }

        internal sealed class Permit : IDisposable
        {
            private CheckpointWriteAdmission _owner;
            internal Permit(CheckpointWriteAdmission owner) { _owner = owner; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
