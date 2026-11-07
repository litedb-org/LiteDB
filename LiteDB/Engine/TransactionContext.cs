using System;
using System.Threading;

namespace LiteDB.Engine
{
    // Synchronous binding only. Facade identity is distinct from the host and lease.
    // Ordinary entry points must mask binding for their entire call, including callbacks.
    internal sealed class TransactionContext
    {
        [ThreadStatic] private static TransactionContext _current;
        [ThreadStatic] private static Ticket _ticket;
        private int _executing;
        internal readonly LiteEngine Engine;
        internal readonly object Host;
        internal readonly object Facade;
        internal readonly TransactionLease Lease;
        internal TransactionService Transaction;
        internal bool Aborted;
        internal volatile LiteTransactionState Outcome = LiteTransactionState.Active;

        internal TransactionContext(LiteEngine engine, object host, object facade)
        {
            Engine = engine;
            Host = host;
            Facade = facade;
            Lease = TransactionLease.Handle(facade);
        }

        internal static TransactionContext For(LiteEngine engine) =>
            ReferenceEquals(_current?.Engine, engine) ? _current : null;

        internal static TransactionContext For(TransactionMonitor monitor) =>
            ReferenceEquals(_current?.Facade, monitor.Owner) ? _current : null;

        internal static bool IsThreadClear => _current == null && _ticket == null;

        internal T Execute<T>(Func<T> action)
        {
            if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
                throw new InvalidOperationException("A handle already has an executing operation.");
            var previous = _current;
            var previousTicket = _ticket;
            try
            {
                Engine.ValidateHandleContext(this);
                if (Transaction == null || Transaction.State != TransactionState.Active)
                    throw new InvalidOperationException("The handle transaction is no longer active.");
                _current = this;
                _ticket = null;
                return action();
            }
            finally
            {
                _current = previous;
                _ticket = previousTicket;
                Volatile.Write(ref _executing, 0);
            }
        }

        internal T Invoke<T>(Func<LiteEngine, T> action) => Execute(() =>
        {
            using (Dispatch(Engine)) return action(Engine);
        });

        internal static T Unbound<T>(Func<T> action)
        {
            var previous = _current;
            var previousTicket = _ticket;
            try { _current = null; _ticket = null; return action(); }
            finally { _current = previous; _ticket = previousTicket; }
        }

        internal static IDisposable Enter(TransactionContext context) => new Binding(context);

        private sealed class Binding : IDisposable
        {
            private readonly TransactionContext _previous = _current;
            private readonly Ticket _previousTicket = _ticket;
            internal Binding(TransactionContext context)
            {
                _current = context;
                _ticket = null;
            }
            public void Dispose() { _current = _previous; _ticket = _previousTicket; }
        }

        internal static DispatchScope Dispatch(LiteEngine engine) => new DispatchScope(engine);

        internal static bool ConsumeDispatch(LiteEngine engine)
        {
            var ticket = _ticket;
            if (ticket == null || !ReferenceEquals(ticket.Engine, engine) ||
                !ReferenceEquals(ticket.Context, For(engine))) return false;
            engine.ValidateHandleContext(ticket.Context);
            _ticket = null;
            return true;
        }

        private sealed class Ticket
        {
            internal readonly LiteEngine Engine;
            internal readonly TransactionContext Context;
            internal Ticket(LiteEngine engine, TransactionContext context) { Engine = engine; Context = context; }
        }

        internal readonly struct DispatchScope : IDisposable
        {
            private readonly Ticket _previous;
            internal DispatchScope(LiteEngine engine)
            {
                _previous = _ticket;
                var context = For(engine);
                _ticket = context == null ? null : new Ticket(engine, context);
            }
            public void Dispose() => _ticket = _previous;
        }
    }
}
