using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Holds the shared mutex on a dedicated thread on behalf of one owner thread,
    /// so the owner's operations keep one engine without reacquiring the mutex.
    /// A Mutex can only be released by the thread that acquired it. A recursion
    /// kept on the owner's own thread would wedge every other thread and process
    /// until the owner happened to call back in; this holder can be ended by any
    /// thread. The hold ends when requested, when the owner thread exits, or at the
    /// first moment without operations or holds after the idle or total limit.
    /// Both limits grow with the cost of opening the engine, so ending a pin to let
    /// other processes in never costs the owner more than a fraction of its time.
    /// A waiter ends the pin too: another thread of the instance at the owner's next
    /// idle moment, another process (seen at the turnstile) once the pin has held for
    /// at least the cost of reopening the engine.
    /// </summary>
    internal sealed class SharedMutexPin
    {
        internal static readonly TimeSpan IdleLimit = TimeSpan.FromMilliseconds(100);
        internal static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(1);
        private const int HoldPerOpen = 10;
        // Least hold before a waiting process ends the pin, and at least the reopen cost.
        internal static readonly TimeSpan YieldAfter = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

        private readonly Mutex _mutex;
        private readonly SharedMutexTurnstile _turnstile;
        private readonly Func<bool> _localWaiters;
        private readonly TimeSpan _idleLimit;
        private readonly TimeSpan _holdLimit;
        private readonly Action<SharedMutexPin, bool> _close;
        private readonly object _sync = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly ManualResetEventSlim _acquired = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _released = new ManualResetEventSlim(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Exception _error;
        private bool _accepting = true;
        private bool _requested;
        private bool _forced;
        // Another process queued at the turnstile; probed by the holder at most every Poll.
        private bool _waiterSeen;
        private TimeSpan _lastProbe;
        private TimeSpan _yield;
        // Operations executing now, and readers or transactions spanning calls.
        private int _operations = 1;
        private int _holds;
        private TimeSpan _lastUse;
        private TimeSpan _readyAt;
        private TimeSpan _idle;
        private TimeSpan _hold;

        private SharedMutexPin(Mutex mutex, SharedMutexTurnstile turnstile, Func<bool> localWaiters,
            Action<SharedMutexPin, bool> close, TimeSpan idleLimit, TimeSpan holdLimit)
        {
            _mutex = mutex;
            _turnstile = turnstile;
            _localWaiters = localWaiters;
            _idleLimit = idleLimit;
            _holdLimit = holdLimit;
            _close = close;
            this.Owner = Thread.CurrentThread;
        }

        public Thread Owner { get; }

        public bool RecoveredAbandonedOwner { get; private set; }

        /// <summary>Set once the owner counted this pin as an engine user.</summary>
        public bool Counted { get; private set; }

        /// <summary>
        /// Called by the owner once the engine is open and counted. Idle and total
        /// limits start now and cover at least one, respectively ten, times the cost
        /// of closing and reopening the engine.
        /// </summary>
        public void MarkReady(TimeSpan cycleCost)
        {
            lock (_sync)
            {
                this.Counted = true;
                _readyAt = _lastUse = _clock.Elapsed;
                _idle = cycleCost > _idleLimit ? cycleCost : _idleLimit;
                var hold = TimeSpan.FromTicks(cycleCost.Ticks * HoldPerOpen);
                _hold = hold > _holdLimit ? hold : _holdLimit;
                _yield = cycleCost > YieldAfter ? cycleCost : YieldAfter;
            }
        }

        /// <summary>
        /// Acquire the mutex on a new holder thread, blocking like WaitOne. The
        /// calling operation is already entered. <paramref name="close"/> runs on
        /// the holder, before release; its flag reports an abandoned or forced end.
        /// </summary>
        public static SharedMutexPin Acquire(Mutex mutex, SharedMutexTurnstile turnstile, Func<bool> localWaiters,
            Action<SharedMutexPin, bool> close, TimeSpan idleLimit, TimeSpan holdLimit)
        {
            var pin = new SharedMutexPin(mutex, turnstile, localWaiters, close, idleLimit, holdLimit);
            var holder = new Thread(pin.Hold) { IsBackground = true, Name = "LiteDB shared mutex holder" };
            holder.Start();
            pin._acquired.Wait();
            if (pin._error != null)
            {
                holder.Join();
                ExceptionDispatchInfo.Capture(pin._error).Throw();
            }
            return pin;
        }

        /// <summary>
        /// Enter an operation on the owner thread while the pin still holds. Once
        /// an end is due, a new top-level operation is refused so a tight loop
        /// cannot keep the holder from ever finding an idle moment. Nested
        /// operations, and operations under the owner's open holds, still enter:
        /// the pin cannot end before they complete.
        /// </summary>
        public bool TryEnter()
        {
            if (!ReferenceEquals(Thread.CurrentThread, this.Owner)) return false;
            lock (_sync)
            {
                if (!_accepting) return false;
                if (_operations == 0 && _holds == 0 && this.ShouldEnd())
                {
                    _accepting = false;
                    _signal.Set();
                    return false;
                }
                _operations++;
                return true;
            }
        }

        /// <summary>End an operation, or a hold when <paramref name="hold"/> is set.</summary>
        public void Exit(bool hold)
        {
            lock (_sync)
            {
                if (hold) _holds--;
                else _operations--;
                _lastUse = _clock.Elapsed;
            }
            _signal.Set();
        }

        /// <summary>Turn the current operation into a hold that outlives the call.</summary>
        public void ToHold()
        {
            lock (_sync)
            {
                _operations--;
                _holds++;
            }
        }

        /// <summary>
        /// End the pin at its next moment without operations (and, unless forced,
        /// without holds). Forcing closes the engine under open readers and
        /// transactions, as disposing the database does.
        /// </summary>
        public void RequestRelease(bool force)
        {
            lock (_sync)
            {
                _requested = true;
                _forced |= force;
            }
            _signal.Set();
        }

        /// <summary>
        /// True when the pin ends without waiting on the calling thread, so the
        /// caller may block until it is released.
        /// </summary>
        public bool CanWaitFrom(Thread thread)
        {
            lock (_sync)
            {
                if (ReferenceEquals(thread, this.Owner) && _operations > 0) return false;
                return _forced || _holds == 0;
            }
        }

        /// <summary>Wait until the holder closed the engine and released the mutex.</summary>
        public void WaitReleased()
        {
            _released.Wait();
            if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
        }

        private void Hold()
        {
            try
            {
                try { _turnstile.Wait(_mutex); }
                catch (AbandonedMutexException) { this.RecoveredAbandonedOwner = true; }
            }
            catch (Exception ex)
            {
                _error = ex;
                _acquired.Set();
                _released.Set();
                return;
            }

            _lastUse = _clock.Elapsed;
            _acquired.Set();
            var abandoned = this.WaitForEnd();

            Exception error = null;
            try { _close(this, abandoned); }
            catch (Exception ex) { error = ex; }
            try { _mutex.ReleaseMutex(); }
            catch (Exception ex) { error = error ?? ex; }
            _error = error;
            _released.Set();
        }

        /// <summary>Returns true for an owner that exited or a forced end.</summary>
        private bool WaitForEnd()
        {
            while (true)
            {
                bool probe;
                lock (_sync)
                {
                    var ownerExited = !this.Owner.IsAlive;
                    var drained = !_accepting && _operations == 0;
                    if (ownerExited || drained || this.ShouldEnd())
                    {
                        _accepting = false;
                        return ownerExited || _forced;
                    }
                    var now = _clock.Elapsed;
                    probe = _accepting && !_waiterSeen && now - _lastProbe >= Poll;
                    if (probe) _lastProbe = now;
                }
                // Outside the lock: the owner's operations never wait for a probe.
                if (probe && _turnstile.HasWaiter())
                {
                    lock (_sync) _waiterSeen = true;
                    continue;
                }
                _signal.WaitOne(Poll);
            }
        }

        private bool ShouldEnd()
        {
            if (_operations > 0) return false;
            if (_forced) return true;
            if (_holds > 0) return false;
            if (_requested || !this.Counted || _localWaiters()) return true;
            var now = _clock.Elapsed;
            if (_waiterSeen && now - _readyAt >= _yield) return true;
            return now - _lastUse >= _idle || now - _readyAt >= _hold;
        }
    }
}
