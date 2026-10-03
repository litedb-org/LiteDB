using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Utils;

namespace LiteDB
{
    public partial class SharedEngine
    {
        // Leased streaming readers of this instance, per creating thread.
        private readonly Dictionary<int, int> _localReaders = new Dictionary<int, int>();
        // Holder of the mutex for the thread iterating a leased reader; null when none.
        private volatile SharedMutexPin _pin;
        // Time the last pin took to close its engine; ending the next pin costs as much.
        private TimeSpan _lastPinClose;

#if DEBUG || TESTING
        internal TimeSpan PinIdleLimit { get; set; } = SharedMutexPin.IdleLimit;

        internal TimeSpan PinHoldLimit { get; set; } = SharedMutexPin.HoldLimit;

        internal SharedMutexPin Pin => _pin;
#else
        private TimeSpan PinIdleLimit => SharedMutexPin.IdleLimit;

        private TimeSpan PinHoldLimit => SharedMutexPin.HoldLimit;
#endif

        private int AddLocalReader()
        {
            var thread = Environment.CurrentManagedThreadId;
            lock (_localReaders)
            {
                _localReaders.TryGetValue(thread, out var count);
                _localReaders[thread] = count + 1;
            }
            return thread;
        }

        private void RemoveLocalReader(int owner)
        {
            lock (_localReaders)
            {
                if (--_localReaders[owner] > 0) return;
                _localReaders.Remove(owner);
            }

            // Any thread may dispose the owner's last reader and so end its pin.
            var pin = _pin;
            if (pin != null && pin.Owner.ManagedThreadId == owner)
            {
                pin.RequestRelease(force: false);
                if (!pin.CanWaitFrom(Thread.CurrentThread)) return;
                pin.WaitReleased();
            }
            this.CheckpointAfterLastReader();
        }

        private bool HasLocalReaders(int thread)
        {
            lock (_localReaders) return _localReaders.ContainsKey(thread);
        }

        /// <summary>
        /// Run a write while owning the mutex. A write issued by a thread that is
        /// still iterating one of this instance's leased readers pins the engine:
        /// a holder thread keeps the mutex between that thread's calls, as the
        /// pre-v13 reader did. Otherwise every such write would reopen and replay
        /// a WAL that the open reader keeps growing, which is quadratic in the loop.
        /// </summary>
        private T WriteDatabase<T>(Func<T> write, bool scoped = false) => this.Call(() =>
        {
            var pin = _pin;
            var use = pin != null && pin.TryEnter() ? pin
                : this.CanPin() ? this.StartPin()
                : this.OpenDatabase(scoped && this.CanScope, writing: true);
            try
            {
                this.StartWriterPressure();
                return write();
            }
            finally
            {
                this.CloseDatabase(use);
            }
        });

        /// <summary>
        /// A thread iterating a leased reader pins; an ending pin of its own is
        /// replaced directly. A holder cannot acquire a mutex this thread owns.
        /// </summary>
        private bool CanPin() =>
            !_transactionRunning && this.HasLocalReaders(Environment.CurrentManagedThreadId) && !_owner.IsOwnedByCurrentThread;

        /// <summary>
        /// Acquire the mutex on a holder thread and open the engine for this thread.
        /// Returns the pin with the calling operation entered.
        /// </summary>
        private SharedMutexPin StartPin()
        {
            this.ThrowIfCallerRetainsOwnership();
            this.RetireCoordinatedReads();
            var other = _pin;
            if (other != null) other.RequestRelease(force: false);

            // A pin that ended for a waiting thread of this instance must not be replaced
            // ahead of it: let the waiters take the mutex first. This cannot deadlock. A
            // pin starts only where CanPin holds, so this thread does not own the
            // connection's mutex ownership; a pin it could not enter has stopped accepting,
            // which it does only without operations in flight, so this thread is not
            // inside one of its operations either. Nothing a waiter needs is held here.
            this.WaitForMutexWaiters();
            SharedMutexPin pin;
            // Counted while acquiring, so that another pin of this instance ends for us too.
            this.AddMutexWaiter();
            try
            {
                pin = SharedMutexPin.Acquire(_mutex, _turnstile, this.HasMutexWaiters, this.ClosePin, this.PinIdleLimit, this.PinHoldLimit);
            }
            finally
            {
                this.RemoveMutexWaiter();
            }
            try
            {
                // The holder owns the mutex on behalf of this thread.
                if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SharedEngine));
                RejectAbandonedTransaction();
                var open = Stopwatch.StartNew();
                if (_engine == null) this.OpenEngine(pin.RecoveredAbandonedOwner, writing: true);
                _databaseUsers++;
                pin.MarkReady(open.Elapsed + _lastPinClose);
                lock (_useLock)
                {
                    // Dispose reads the pin under this lock after marking itself disposed:
                    // it either sees this pin and ends it, or this start is refused.
                    if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SharedEngine));
                    _pin = pin;
                }
                return pin;
            }
            catch
            {
                pin.Exit(hold: false);
                pin.RequestRelease(force: false);
                pin.WaitReleased();
                throw;
            }
        }

        /// <summary>
        /// Runs on the pin's holder thread while it still owns the mutex. An
        /// abandoned or forced end closes the engine even under the owner's open
        /// readers and transactions; nothing else can use it after the release.
        /// </summary>
        [TeardownPath("SharedEngine.ClosePin", TeardownDisposition.Propagated | TeardownDisposition.Discarded,
            "Runs on the pin holder: a failure becomes the pin's error, rethrown by WaitReleased to a waiter; dropped when nobody waits.")]
        private void ClosePin(SharedMutexPin pin, bool abandoned)
        {
            using (this.OwnershipFrame(HolderRetains)) this.ClosePinEngine(pin, abandoned);
        }

        private void ClosePinEngine(SharedMutexPin pin, bool abandoned)
        {
            if (ReferenceEquals(_pin, pin)) _pin = null;
            if (!pin.Counted) return;

            if (abandoned)
            {
                _databaseUsers = 0;
                // An exited owner's transaction is reported to the next caller;
                // a forced end (Dispose) discards a live one.
                if (pin.Owner.IsAlive && ReferenceEquals(_transactionUse, pin))
                {
                    _transactionUse = null;
                    _transactionRunning = false;
                    _transactionThreadId = 0;
                }
            }
            else _databaseUsers--;

            if (_databaseUsers == 0 && (abandoned || !_transactionRunning) && _engine != null)
            {
                var engine = _engine;
                _engine = null;
                var close = Stopwatch.StartNew();
                this.CloseRetainedCore(engine);
                _lastPinClose = close.Elapsed;
            }
            var disposed = Volatile.Read(ref _disposed) != 0;
            TeardownSteps.Before("SharedEngine.ClosePin.coordination", disposed);
            if (disposed) this.DisposeCoordination();
            TeardownSteps.After("SharedEngine.ClosePin.coordination", disposed);
        }

        /// <summary>
        /// Writes made while readers were leased leave a WAL that only a full
        /// checkpoint can remove. Once the last reader anywhere is gone, close an
        /// engine here so the data file alone is again the whole database, as it
        /// was after the pre-v13 reader closed its engine. Best effort: it never
        /// waits for another owner of the mutex, whose own close checkpoints.
        /// </summary>
        [TeardownPath("SharedEngine.CheckpointAfterLastReader", TeardownDisposition.Discarded | TeardownDisposition.Propagated,
            "I/O and access failures are swallowed ('Reader disposal must not fail because the environment refused this cleanup'); " +
            "database errors surface (SharedEngine.Readers.cs).")]
        private void CheckpointAfterLastReader()
        {
            if (_settings.ReadOnly || !LogHasContent(_settings.Filename)) return;
            if (!_owner.TryEnter(out var abandoned, scoped: this.CanScope)) return;
            if (abandoned)
            {
                // Leave abandoned-owner recovery to the next ordinary open.
                _owner.Exit();
                return;
            }

            var depth = this.AdmittedDepth();
            try
            {
                lock (_useLock)
                {
                    // A disposed connection's final close does this cleanup itself.
                    if (Volatile.Read(ref _disposed) != 0) return;
                    this.AdmitLocked();
                }
                if (_engine != null || _transactionRunning || _readers.OldestVersion().HasValue) return;
                TeardownSteps.Before("SharedEngine.CheckpointAfterLastReader.close-finally");
                using (this.OwnershipFrame(this.CallRetains)) this.CloseFinally();
                TeardownSteps.After("SharedEngine.CheckpointAfterLastReader.close-finally");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Reader disposal must not fail because the environment refused this
                // cleanup. The WAL remains authoritative and the next open recovers
                // and checkpoints it. Database errors, such as a damaged WAL, surface.
            }
            finally
            {
                this.EndAdmissions(depth);
                _owner.Exit();
            }
        }

        /// <summary>How long a final close waits for another connection's use of the mutex.</summary>
        internal static readonly TimeSpan DisposeCheckpointWait = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The connection's final close: checkpoint what its operations left below the
        /// close threshold. The current owner of the mutex may be a read-only connection,
        /// which never checkpoints, so wait for it instead of leaving the WAL to it. The
        /// wait is bounded: this thread may itself keep that owner busy (another
        /// connection's operation on the same thread). A WAL another close removed ends it.
        /// </summary>
        [TeardownPath("SharedEngine.CheckpointOnDispose", TeardownDisposition.Discarded | TeardownDisposition.Propagated,
            "I/O, access and LiteException failures are swallowed ('Dispose must not fail because of this cleanup'); " +
            "other exceptions propagate (SharedEngine.Readers.cs).")]
        private void CheckpointOnDispose()
        {
            if (_settings.ReadOnly || !LogHasContent(_settings.Filename)) return;
            if (!this.TryEnterForDispose(out var abandoned)) return;
            try
            {
                if (abandoned || _engine != null || _transactionRunning) return;
                TeardownSteps.Before("SharedEngine.CheckpointOnDispose.close-finally");
                using (this.OwnershipFrame(this.CallRetains)) this.CloseFinally();
                TeardownSteps.After("SharedEngine.CheckpointOnDispose.close-finally");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is LiteException)
            {
                // Dispose must not fail because of this cleanup, including a database the
                // open refuses (an incomplete rebuild, damage): the WAL remains authoritative
                // and the next open reports the same condition. A close checkpoint's own
                // errors were never raised by Dispose either.
            }
            finally
            {
                _owner.Exit();
            }
        }

        private bool TryEnterForDispose(out bool abandoned)
        {
            var waited = Stopwatch.StartNew();
#if DEBUG || TESTING
            // A bounded poll for this connection's ownership and the OS mutex; it gives up after DisposeCheckpointWait.
            using (LiteDB.Utils.WaitGraph.Wait(_owner.GraphOwnership, LiteDB.Utils.WaitBound.After(DisposeCheckpointWait),
                "SharedEngine.TryEnterForDispose", this, also: _owner.GraphMutexResource))
#endif
            while (true)
            {
                if (_owner.TryEnter(out abandoned, scoped: this.CanScope)) return true;
                if (waited.Elapsed >= DisposeCheckpointWait || !LogHasContent(_settings.Filename)) return false;
#if DEBUG || TESTING
                LiteDB.Utils.WaitGraph.Recheck();
#endif
                Thread.Sleep(10);
            }
        }

        /// <summary>Open an engine only to close it with its checkpoint. The caller owns the mutex.</summary>
        private void CloseFinally()
        {
            this.OpenEngine(false, final: true, writing: true);
            var engine = _engine;
            _engine = null;
            this.CloseRetainedCore(engine, final: true);
        }

        private static bool LogHasContent(string filename)
        {
            var log = new FileInfo(FileHelper.GetLogFile(filename));
            return log.Exists && log.Length > 0;
        }
    }
}
