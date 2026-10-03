using System;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Logical identity of one explicit transaction handle. It is installed on the
    /// executing thread only for the duration of a bound synchronous call; the engine
    /// resolves the handle's transaction through it instead of the legacy thread slot.
    /// </summary>
    internal sealed class TransactionContext
    {
        [ThreadStatic] private static TransactionContext _executing;
        [ThreadStatic] private static LiteEngine _dispatch;
        // Unlike binding, execution dependencies survive ordinary facade calls. The
        // chain is synchronous, restores on every exit, and never follows a Task.
        [ThreadStatic] private static TransactionContext _dependency;
        private TransactionContext _dependencyParent;
        private readonly string _sharedMutexName;
        internal readonly LiteEngine Engine;
        internal readonly TransactionSlot Slot = new TransactionSlot();
        internal TransactionService Transaction;
        internal volatile LiteTransactionState Outcome = LiteTransactionState.Active;
        // Ordinary callbacks suppress binding, but still depend on this executing
        // handle returning before its locks can be released.
        internal volatile Thread ExecutingThread;

        internal TransactionContext(LiteEngine engine, string sharedMutexName = null)
        {
            Engine = engine;
            _sharedMutexName = sharedMutexName;
        }

        /// <summary>
        /// Refuse a Shared native wait from inside an executing handle of the same database:
        /// the handle's writer ownership is released only after this callback returns.
        /// </summary>
        internal static void ThrowIfSharedWait(string mutexName)
        {
            for (var current = _dependency; current != null; current = current._dependencyParent)
                if (string.Equals(current._sharedMutexName, mutexName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Cannot wait for shared writer ownership from inside a transaction handle callback for the same database.");
        }

        /// <summary>Whether no handle is bound, dispatched or executing on this thread.</summary>
        internal static bool IsThreadClear => _executing == null && _dispatch == null && _dependency == null;

        /// <summary>The handle bound to <paramref name="engine"/> on this thread, if any.</summary>
        internal static TransactionContext For(LiteEngine engine) =>
            engine != null && ReferenceEquals(_executing?.Engine, engine) ? _executing : null;

        internal static Scope Enter(TransactionContext transaction) => new Scope(transaction);
        internal static DispatchScope Dispatch(LiteEngine engine) => new DispatchScope(engine);

        internal static bool ConsumeDispatch(LiteEngine engine)
        {
            if (!ReferenceEquals(_dispatch, engine)) return false;
            _dispatch = null;
            return true;
        }

        // A one-use ticket authorizes a composed engine entry. It is consumed before any
        // user callback, so raw public reentry cannot acquire the enclosing handle's identity.
        internal readonly struct DispatchScope : IDisposable
        {
            private readonly LiteEngine _previous;
            internal DispatchScope(LiteEngine engine) { _previous = _dispatch; _dispatch = engine; }
            public void Dispose() => _dispatch = _previous;
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly TransactionContext _previous, _transaction;
            private readonly Thread _previousThread;
            private readonly TransactionContext _previousDependency, _previousParent;

            internal Scope(TransactionContext transaction)
            {
                _previous = _executing;
                _transaction = transaction;
                _previousThread = transaction?.ExecutingThread;
                _previousDependency = _dependency;
                _previousParent = transaction?._dependencyParent;
                // Composed dispatch nests the same handle. Do not link it to itself.
                if (transaction != null && !ReferenceEquals(transaction, _dependency))
                {
                    transaction._dependencyParent = _dependency;
                    _dependency = transaction;
                }
                if (transaction != null) transaction.ExecutingThread = Thread.CurrentThread;
                _executing = transaction;
            }

            public void Dispose()
            {
                _executing = _previous;
                _dependency = _previousDependency;
                if (_transaction != null) _transaction._dependencyParent = _previousParent;
                if (_transaction != null) _transaction.ExecutingThread = _previousThread;
            }
        }
    }

    /// <summary>The transaction resolved for one owner: a legacy thread or an explicit handle.</summary>
    internal sealed class TransactionSlot
    {
        internal TransactionService Transaction;
        internal bool ExplicitAborted;
        // A slot's owner never changes (its thread, or its handle): allocate it once.
        internal TransactionOwner Owner;
    }
}
