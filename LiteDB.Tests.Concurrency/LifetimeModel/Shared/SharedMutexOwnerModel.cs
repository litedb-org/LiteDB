using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel.Shared
{
    /// <summary>
    /// <c>SharedMutexOwner</c> (LiteDB/Client/Shared/SharedMutexOwner.cs): the connection's gate
    /// semaphore, its logical owner thread with recursion and generation, the holder thread that
    /// takes and releases the OS mutex for non-scoped owners, and the scoped (direct) ownership of
    /// <c>SharedMutexScope</c>. One connection in one process: whenever the gate is free the OS
    /// mutex is free too (every release frees the mutex before the gate), so the OS mutex and the
    /// turnstile never block in this model. Owner-thread exit (abandonment) is not modeled.
    /// </summary>
    internal sealed class SharedMutexOwnerModel
    {
        internal enum Command { None, Acquire, TryAcquire, Release, ReleaseAndOpenGate }

        /// <summary>SharedMutexOwner.HolderIdle: an idle holder that owns nothing exits.</summary>
        private const int HolderIdle = 1000;

        private readonly IModelHost _host;
        private readonly HashSet<int> _scopedThreads = new HashSet<int>();
        private Command _command;
        private bool _done;
        private bool _acquired;
        private bool _holderAlive;
        private int? _sendLock;

        public SharedMutexOwnerModel(IModelHost host) => _host = host;

        /// <summary>SemaphoreSlim _gate = new SemaphoreSlim(1, 1) (SharedMutexOwner.cs:35).</summary>
        public int GatePermits { get; private set; } = 1;

        public int? Owner { get; private set; }

        public int Recursion { get; private set; }

        public int Generation { get; private set; }

        /// <summary>SharedMutexScope.Owner: the thread that took the OS mutex directly.</summary>
        public int? ScopeOwner { get; private set; }

        /// <summary>_held: the holder thread owns the OS mutex.</summary>
        public bool Held { get; private set; }

        /// <summary>_released (ManualResetEventSlim(true)): no posted release is in flight.</summary>
        public bool Released { get; private set; } = true;

        public bool IsOwnedBy(ModelThread t) => this.Owner == t.Id;

        /// <summary>SharedMutexOwner.Enter (SharedMutexOwner.cs:128-136).</summary>
        public IEnumerable<Step> Enter(ModelThread t, bool scoped)
        {
            if (this.TryRecurse(t)) yield break;
            yield return Step.Wait("SharedMutexOwner.cs:131 Enter waits for the connection gate (_gate.Wait(Poll) loop)", () => this.GatePermits > 0);
            this.GatePermits--;
            if (scoped && !_scopedThreads.Contains(t.Id))
            {
                yield return Step.At("SharedMutexOwner.cs:74 TakeDirect: SharedMutexScope.Take on the calling thread");
                this.TakeDirect(t);
                yield break;
            }
            foreach (var step in this.TakeGate(t, Command.Acquire)) yield return step;
        }

        /// <summary>SharedMutexOwner.TryEnter (SharedMutexOwner.cs:142-159). Sets <paramref name="entered"/>.</summary>
        public IEnumerable<Step> TryEnter(ModelThread t, bool scoped, Ref<bool> entered)
        {
            entered.Value = true;
            if (this.TryRecurse(t)) yield break;
            yield return Step.Wait("SharedMutexOwner.cs:147 TryEnter: WaitForRelease (_released.Wait)", () => this.Released);
            yield return Step.At("SharedMutexOwner.cs:148 TryEnter: _gate.Wait(0)");
            if (this.GatePermits == 0)
            {
                // ReleaseIfOwnerExited finds no exited owner; wait for our own release, try once more.
                yield return Step.Wait("SharedMutexOwner.cs:153 TryEnter: WaitForRelease", () => this.Released);
                if (this.GatePermits == 0)
                {
                    entered.Value = false;
                    yield break;
                }
            }
            this.GatePermits--;
            if (scoped && !_scopedThreads.Contains(t.Id))
            {
                this.TakeDirect(t);
                yield break;
            }
            foreach (var step in this.TakeGate(t, Command.TryAcquire)) yield return step;
        }

        /// <summary>SharedMutexOwner.Exit (SharedMutexOwner.cs:165-209). The caller yields before it.</summary>
        public IEnumerable<Step> Exit(ModelThread t, int generation = -1)
        {
            var direct = this.ScopeOwner;
            if (generation < 0 && this.Owner != null && this.Owner != t.Id) yield break; // :174
            if (this.Owner == null || (generation >= 0 && generation != this.Generation))
            {
                if (this.Owner != null || direct != t.Id) yield break; // :179
            }
            else
            {
                if (this.Recursion > 1)
                {
                    this.Recursion--;
                    yield break;
                }
                if (direct != null && direct != t.Id)
                {
                    t.Fault = "InvalidOperationException(scoped ownership ended on another thread)"; // :191-192
                    yield break;
                }
                this.Recursion = 0;
                this.Owner = null;
                this.Generation++;
                if (direct == null) this.Released = false; // :196
            }
            this.ScopeOwner = null;
            if (direct != null)
            {
                yield return Step.At("SharedMutexOwner.cs:202 SharedMutexScope.Release, then _gate.Release");
                _scopedThreads.Remove(t.Id);
                this.ReleaseGate();
                yield break;
            }
            foreach (var step in this.Post(t, Command.ReleaseAndOpenGate)) yield return step; // :208
        }

        /// <summary>SharedMutexOwner.ReleaseAll (SharedMutexOwner.cs:215-230), as Dispose calls it.</summary>
        public IEnumerable<Step> ReleaseAll(ModelThread t)
        {
            if (this.Owner == null) yield break;
            this.Owner = null;
            this.Recursion = 0;
            this.Generation++;
            if (this.ScopeOwner != null) yield break; // :225 the scoped thread releases when its call ends
            foreach (var step in this.Send(t, Command.Release, out _)) yield return step;
            this.ReleaseGate();
        }

        /// <summary>SharedMutexOwner.WaitForRelease (SharedMutexOwner.cs:237).</summary>
        public Step WaitForRelease(string caller) => Step.Wait($"{caller}: SharedMutexOwner.WaitForRelease (_released.Wait)", () => this.Released);

        private bool TryRecurse(ModelThread t)
        {
            if (this.Owner != t.Id) return false; // :243
            this.Recursion++;
            return true;
        }

        private void TakeDirect(ModelThread t)
        {
            _scopedThreads.Add(t.Id);
            this.Owner = t.Id;
            this.ScopeOwner = t.Id;
            this.Recursion = 1;
        }

        /// <summary>SharedMutexOwner.TakeGate (SharedMutexOwner.cs:250-269).</summary>
        private IEnumerable<Step> TakeGate(ModelThread t, Command command)
        {
            foreach (var step in this.Send(t, command, out var acquired)) yield return step;
            this.Owner = t.Id;
            this.Recursion = 1;
        }

        /// <summary>SharedMutexOwner.Send (SharedMutexOwner.cs:336-359): publish, signal, wait for _done.</summary>
        private IEnumerable<Step> Send(ModelThread t, Command command, out Ref<bool> acquired)
        {
            acquired = new Ref<bool>();
            return this.SendCore(t, command, acquired);
        }

        private IEnumerable<Step> SendCore(ModelThread t, Command command, Ref<bool> acquired)
        {
            yield return Step.Wait("SharedMutexOwner.cs:338 Send: lock(_send)", () => _sendLock == null);
            _sendLock = t.Id;
            this.Publish(command);
            _done = false;
            yield return Step.Wait($"SharedMutexOwner.cs:350 Send({command}) waits for the holder (_done.Wait)", () => _done);
            acquired.Value = _acquired;
            _sendLock = null;
        }

        /// <summary>SharedMutexOwner.Post (SharedMutexOwner.cs:306-322): nobody waits for the holder.</summary>
        private IEnumerable<Step> Post(ModelThread t, Command command)
        {
            yield return Step.Wait("SharedMutexOwner.cs:308 Post: lock(_send)", () => _sendLock == null);
            this.Publish(command);
        }

        private void Publish(Command command)
        {
            // One command is pending at most (SharedMutexOwner.cs:333-334); a second would be lost.
            _host.Assert(_command == Command.None, $"Model invariant: holder command {_command} overwritten by {command}.");
            if (!_holderAlive)
            {
                // EnsureHolder (SharedMutexOwner.cs:324-329).
                _holderAlive = true;
                _host.Spawn("holder", this.Holder, daemon: true);
            }
            _command = command;
        }

        private void ReleaseGate()
        {
            this.GatePermits++;
            _host.Assert(this.GatePermits <= 1, "Model invariant: SemaphoreFullException, the connection gate was released twice.");
        }

        /// <summary>SharedMutexOwner.Run (SharedMutexOwner.cs:361-436), the holder thread.</summary>
        private IEnumerable<Step> Holder(ModelThread h)
        {
            while (true)
            {
                const string site = "SharedMutexOwner.cs:366 holder waits for a command";
                // Holding the mutex, it only polls for an exited owner, which the model has none of.
                yield return this.Held
                    ? Step.Wait(site, () => _command != Command.None)
                    : Step.TimedWait(site, HolderIdle, () => _command != Command.None);
                if (h.TimedOut)
                {
                    if (_command != Command.None || this.Held) continue;
                    _holderAlive = false; // :380-384 idle holder exits
                    yield break;
                }

                var command = _command;
                yield return Step.At($"SharedMutexOwner.cs:404 holder runs {command}");
                var acquired = command == Command.Acquire || command == Command.TryAcquire;
                if (!acquired) this.Held = false; // ReleaseMutex (:457-465)
                else this.Held = true; // WaitMutex: the gate holder finds the OS mutex free
                _acquired = acquired;
                _command = Command.None;
                if (command == Command.ReleaseAndOpenGate)
                {
                    yield return Step.At("SharedMutexOwner.cs:432 holder opens the gate after the posted release");
                    this.ReleaseGate();
                    this.Released = true;
                }
                else _done = true;
            }
        }

        public override string ToString() =>
            $"owner(gate={this.GatePermits}, owner={this.Owner?.ToString() ?? "-"}, recursion={this.Recursion}, generation={this.Generation}, " +
            $"scope={this.ScopeOwner?.ToString() ?? "-"}, held={this.Held}, released={this.Released}, command={_command})";
    }
}
