using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// A connection's ownership of its named mutex, independent of the calling thread.
    /// A Mutex can only be released by the thread that acquired it, so a reader or
    /// connection disposed on another thread (an await continuation, a consumer
    /// thread) used to throw and leave the mutex held until the acquiring thread
    /// exited, blocking every process. Here a holder thread acquires and releases the
    /// OS mutex, and one logical owner thread holds it recursively in this process.
    /// Any thread may end the ownership. When the owner thread exits while holding
    /// it, the holder closes the connection's state and releases the mutex, as the
    /// OS would have abandoned it; the connection reports the exit on its next call.
    /// </summary>
    internal sealed class SharedMutexOwner
    {
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
        // A holder that owns nothing exits after this long, so an undisposed
        // connection does not keep a thread (and itself) alive forever.
        private static readonly TimeSpan HolderIdle = TimeSpan.FromSeconds(1);
        private const int SpinCount = 1000;

        private enum Command { None, Acquire, TryAcquire, Release, ReleaseAndOpenGate, ReleaseExitedOwner }

        private readonly Mutex _mutex;
        private readonly Action _ownerExited;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _send = new object();
        private readonly object _sync = new object();
        // The caller spins before blocking: it stays on its core while the holder
        // takes or releases the OS mutex, which takes microseconds. The holder waits
        // between operations of unknown length, so it only spins briefly.
        private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false, SpinCount);
        // Reset while a posted release of this connection is in flight. Until the
        // holder completes it, the gate stays closed and the OS mutex held, although
        // nobody owns the connection any more.
        private readonly ManualResetEventSlim _released = new ManualResetEventSlim(true, SpinCount);
        private Thread _holder;
        private Command _command;
        private bool _acquired;
        private bool _abandoned;
        private Exception _error;
        private bool _held;
        private Thread _owner;
        private int _recursion;
        private int _generation;

        public SharedMutexOwner(Mutex mutex, Action ownerExited)
        {
            _mutex = mutex;
            _ownerExited = ownerExited;
        }

        public Mutex Mutex => _mutex;

#if DEBUG || TESTING
        /// <summary>Runs on the holder before it performs a release posted by <see cref="Exit"/>.</summary>
        internal Action BeforePostedRelease { get; set; }
#endif

        /// <summary>Changes whenever ownership ends, so a stale release is ignored.</summary>
        public int Generation { get { lock (_sync) return _generation; } }

        public bool IsOwnedByCurrentThread
        {
            get { lock (_sync) return ReferenceEquals(_owner, Thread.CurrentThread); }
        }

        /// <summary>
        /// Acquire, or enter recursively on the owner thread. Returns true when the
        /// OS reported the mutex abandoned by another process.
        /// </summary>
        public bool Enter()
        {
            if (this.TryRecurse()) return false;
            while (!_gate.Wait(Poll)) this.ReleaseIfOwnerExited();
            this.TakeGate(Command.Acquire, out var abandoned);
            return abandoned;
        }

        /// <summary>
        /// Enter without waiting for another thread or process. Fails when a live
        /// thread of this connection, or another process, owns the mutex.
        /// </summary>
        public bool TryEnter(out bool abandoned)
        {
            abandoned = false;
            if (this.TryRecurse()) return true;
            // This connection's own release in flight is not another owner.
            this.WaitForRelease();
            if (!_gate.Wait(0))
            {
                if (!this.ReleaseIfOwnerExited() || !_gate.Wait(0)) return false;
            }
            return this.TakeGate(Command.TryAcquire, out abandoned);
        }

        /// <summary>
        /// End one recursion from any thread. With <paramref name="generation"/>,
        /// nothing happens once that ownership already ended.
        /// </summary>
        public void Exit(int generation = -1)
        {
            lock (_sync)
            {
                if (_owner == null || (generation >= 0 && generation != _generation)) return;
                if (--_recursion > 0) return;
                _owner = null;
                _generation++;
                _released.Reset();
            }
            // The caller need not wait: the holder releases the OS mutex and only then
            // opens the gate, so the next owner in this process still waits for it.
            this.Post(Command.ReleaseAndOpenGate);
        }

        /// <summary>
        /// End any ownership, whichever thread owns it, as disposing the connection
        /// does. Later releases of that ownership are ignored. Never throws.
        /// </summary>
        public void ReleaseAll()
        {
            lock (_sync)
            {
                if (_owner == null) return;
                _owner = null;
                _recursion = 0;
                _generation++;
            }
            try { this.Send(Command.Release); }
            catch (Exception) { /* Disposal must not fail; process exit releases the mutex. */ }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Wait until a release posted by <see cref="Exit"/> completed, so that this
        /// connection holds neither the gate nor the OS mutex on its own account.
        /// The holder completes it without waiting for anything else.
        /// </summary>
        public void WaitForRelease() => _released.Wait();

        private bool TryRecurse()
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_owner, Thread.CurrentThread)) return false;
                _recursion++;
                return true;
            }
        }

        /// <summary>Acquire the OS mutex for the calling thread, which holds the gate.</summary>
        private bool TakeGate(Command command, out bool abandoned)
        {
            abandoned = false;
            try
            {
                if (!this.Send(command)) { _gate.Release(); return false; }
                abandoned = _abandoned;
                lock (_sync)
                {
                    _owner = Thread.CurrentThread;
                    _recursion = 1;
                }
                return true;
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        /// <summary>
        /// If the owner thread exited while owning the mutex, have the holder close
        /// the connection's state and release it. Returns true when it did.
        /// </summary>
        private bool ReleaseIfOwnerExited()
        {
            Thread owner;
            lock (_sync) owner = _owner;
            if (owner == null || owner.IsAlive) return false;
            this.Send(Command.ReleaseExitedOwner);
            return true;
        }

        /// <summary>Queue a command whose completion opens the gate; nobody waits for it.</summary>
        private void Post(Command command)
        {
            lock (_send)
            {
                lock (_sync)
                {
                    this.EnsureHolder();
                    _command = command;
                }
                _posted.Set();
            }
        }

        private void EnsureHolder()
        {
            if (_holder != null && _holder.IsAlive) return;
            _holder = new Thread(this.Run) { IsBackground = true, Name = "LiteDB shared mutex owner" };
            _holder.Start();
        }

        /// <summary>
        /// Run a command on the holder thread and wait for it. Only the gate's holder,
        /// or a caller that finds the gate's owner exited, sends; a posted release
        /// opens the gate only after it completed, so one command is pending at most.
        /// </summary>
        private bool Send(Command command)
        {
            lock (_send)
            {
                lock (_sync)
                {
                    this.EnsureHolder();
                    _command = command;
                    _done.Reset();
                }
                _posted.Set();
                _done.Wait();
                lock (_sync)
                {
                    var error = _error;
                    _error = null;
                    if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
                    return _acquired;
                }
            }
        }

        private void Run()
        {
            var idleSince = DateTime.UtcNow;
            while (true)
            {
                _posted.Wait(Poll);
                Command command;
                var ownerExited = false;
                lock (_sync)
                {
                    command = _command;
                    if (command != Command.None) _posted.Reset();
                    else if (_held && _owner != null && !_owner.IsAlive) ownerExited = true;
                    else if (!_held && DateTime.UtcNow - idleSince > HolderIdle)
                    {
                        _holder = null;
                        return;
                    }
                }

                if (command == Command.None)
                {
                    // Nobody waits for this; a caller's command is taken next round.
                    if (ownerExited)
                    {
                        try { this.ReleaseExitedOwner(); }
                        catch (Exception) { /* Retried by the next caller or poll. */ }
                    }
                    continue;
                }

                var acquired = false;
                var abandoned = false;
                var posted = false;
                Exception error = null;
                try
                {
                    switch (command)
                    {
                        case Command.Acquire: acquired = this.WaitMutex(block: true, out abandoned); break;
                        case Command.TryAcquire: acquired = this.WaitMutex(block: false, out abandoned); break;
                        case Command.Release: this.ReleaseMutex(); break;
                        case Command.ReleaseAndOpenGate:
#if DEBUG || TESTING
                            this.BeforePostedRelease?.Invoke();
#endif
                            this.ReleaseMutex();
                            break;
                        case Command.ReleaseExitedOwner: this.ReleaseExitedOwner(); break;
                    }
                }
                catch (Exception ex) { error = ex; }

                lock (_sync)
                {
                    if (command == Command.Acquire || command == Command.TryAcquire) _held = acquired;
                    idleSince = DateTime.UtcNow;
                    _acquired = acquired;
                    _abandoned = abandoned;
                    _command = Command.None;
                    if (command == Command.ReleaseAndOpenGate) posted = true;
                    else _error = error;
                }
                if (posted)
                {
                    _gate.Release();
                    _released.Set();
                }
                else _done.Set();
            }
        }

        private bool WaitMutex(bool block, out bool abandoned)
        {
            abandoned = false;
            try
            {
                // Block without polling: a polling waiter would lose its place among
                // the OS mutex's waiters, such as a pin holder of this connection.
                return block ? _mutex.WaitOne() : _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                abandoned = true;
                return true;
            }
        }

        private void ReleaseMutex()
        {
            lock (_sync)
            {
                if (!_held) return;
                _held = false;
            }
            _mutex.ReleaseMutex();
        }

        /// <summary>On the holder: the owner thread exited while owning the mutex.</summary>
        private void ReleaseExitedOwner()
        {
            lock (_sync)
            {
                if (!_held || _owner == null || _owner.IsAlive) return;
            }
            try { _ownerExited(); }
            catch (Exception) { /* The next open recovers; the mutex must still be released. */ }
            lock (_sync)
            {
                _owner = null;
                _recursion = 0;
                _generation++;
            }
            this.ReleaseMutex();
            _gate.Release();
        }
    }
}
