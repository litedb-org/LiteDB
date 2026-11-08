using System;
using System.Threading;
using System.Collections.Generic;

namespace LiteDB.Engine
{
    // Callback-free state retained with the storage transaction. Host close can
    // drain a public call without rooting its handle, mapper or facade graph.
    internal sealed class HandleCallLifetime
    {
        private readonly object _gate = new object();
        private bool _running, _closing;
        internal bool Running { get { lock (_gate) return _running; } }
        internal void Enter()
        {
            lock (_gate)
            {
                if (_closing) throw LiteException.EngineDisposed();
                if (_running) throw new InvalidOperationException("Overlapping handle storage operation.");
                _running = true;
            }
        }
        internal void Exit()
        {
            lock (_gate) { _running = false; Monitor.PulseAll(_gate); }
        }
        internal void Refuse() { lock (_gate) _closing = true; }
    }

    // Independent of the transaction registry: a terminal call can unregister its
    // transaction before finishing cleanup. The host still waits for that call.
    internal sealed class HandleOperationRegistry
    {
        private readonly HashSet<HandleCallLifetime> _calls = new HashSet<HandleCallLifetime>();
        private bool _closing;
        internal void Enter(HandleCallLifetime call)
        {
            lock (_calls)
            {
                if (_closing) throw LiteException.EngineDisposed();
                call.Enter();
                _calls.Add(call);
            }
        }
        internal void Exit(HandleCallLifetime call)
        {
            lock (_calls) { call.Exit(); _calls.Remove(call); Monitor.PulseAll(_calls); }
        }
        internal void Refuse() { lock (_calls) _closing = true; }
        internal void Drain() { lock (_calls) while (_calls.Count != 0) Monitor.Wait(_calls); }
    }
}
