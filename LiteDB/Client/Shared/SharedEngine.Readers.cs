using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LiteDB.Client.Shared;

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
        private T WriteDatabase<T>(Func<T> write)
        {
            var pin = _pin;
            var use = pin != null && pin.TryEnter() ? pin
                : this.CanPin() ? this.StartPin()
                : this.OpenDatabase();
            try
            {
                return write();
            }
            finally
            {
                this.CloseDatabase(use);
            }
        }

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
                pin = SharedMutexPin.Acquire(_mutex, this.HasMutexWaiters, this.ClosePin, this.PinIdleLimit, this.PinHoldLimit);
            }
            finally
            {
                this.RemoveMutexWaiter();
            }
            try
            {
                // The holder owns the mutex on behalf of this thread.
                RejectAbandonedTransaction();
                var open = Stopwatch.StartNew();
                if (_engine == null) this.OpenEngine(pin.RecoveredAbandonedOwner);
                _databaseUsers++;
                pin.MarkReady(open.Elapsed + _lastPinClose);
                _pin = pin;
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
        private void ClosePin(SharedMutexPin pin, bool abandoned)
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
                engine.Dispose();
                _lastPinClose = close.Elapsed;
            }
        }

        /// <summary>
        /// Writes made while readers were leased leave a WAL that only a full
        /// checkpoint can remove. Once the last reader anywhere is gone, close an
        /// engine here so the data file alone is again the whole database, as it
        /// was after the pre-v13 reader closed its engine. Best effort: it never
        /// waits for another owner of the mutex, whose own close checkpoints.
        /// </summary>
        private void CheckpointAfterLastReader()
        {
            if (_settings.ReadOnly || !LogHasContent(_settings.Filename)) return;
            if (!_owner.TryEnter(out var abandoned)) return;
            if (abandoned)
            {
                // Leave abandoned-owner recovery to the next ordinary open.
                _owner.Exit();
                return;
            }

            try
            {
                if (_engine != null || _transactionRunning || _readers.OldestVersion().HasValue) return;
                this.CloseFinally();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Reader disposal must not fail because the environment refused this
                // cleanup. The WAL remains authoritative and the next open recovers
                // and checkpoints it. Database errors, such as a damaged WAL, surface.
            }
            finally
            {
                _owner.Exit();
            }
        }

        /// <summary>
        /// The connection's final close: checkpoint what its operations left below the
        /// close threshold. Best effort like <see cref="CheckpointAfterLastReader"/>: a
        /// current owner of the mutex is a live connection whose own final close checkpoints.
        /// </summary>
        private void CheckpointOnDispose()
        {
            if (_settings.ReadOnly || !LogHasContent(_settings.Filename)) return;
            if (!_owner.TryEnter(out var abandoned)) return;
            try
            {
                if (abandoned || _engine != null || _transactionRunning) return;
                this.CloseFinally();
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

        /// <summary>Open an engine only to close it with its checkpoint. The caller owns the mutex.</summary>
        private void CloseFinally()
        {
            this.OpenEngine(false);
            var engine = _engine;
            _engine = null;
            engine.Close(final: true);
        }

        private static bool LogHasContent(string filename)
        {
            var log = new FileInfo(FileHelper.GetLogFile(filename));
            return log.Exists && log.Length > 0;
        }
    }
}
