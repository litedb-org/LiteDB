using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace LiteDB.Engine
{
    internal enum TransactionOwnerKind { Legacy, Handle }

    // Facade identity and transaction identity are orthogonal. A lease is never
    // reused: delayed cleanup retains this exact identity and generation.
    internal sealed class TransactionLease
    {
        private static long _generation;
        private int _claimed;
        internal object Facade { get; }
        internal TransactionOwnerToken Token { get; } = new TransactionOwnerToken();
        internal long Generation { get; } = Interlocked.Increment(ref _generation);
        internal TransactionOwnerKind Kind { get; }
        internal Thread Thread { get; }
        internal object Admission => Kind == TransactionOwnerKind.Legacy ? (object)Thread : this;
        internal object LockOwner => Admission;

        private TransactionLease(object facade, TransactionOwnerKind kind)
        {
            Facade = facade ?? throw new ArgumentNullException(nameof(facade));
            Kind = kind;
            Thread = kind == TransactionOwnerKind.Legacy ? System.Threading.Thread.CurrentThread : null;
        }

        internal void Claim()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
                throw new InvalidOperationException("A transaction lease cannot be reused.");
        }

        internal static TransactionLease Legacy(object facade) => new TransactionLease(facade, TransactionOwnerKind.Legacy);
        internal static TransactionLease Handle(object facade) => new TransactionLease(facade, TransactionOwnerKind.Handle);
    }

    internal sealed class ReferenceOwnerComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceOwnerComparer Instance = new ReferenceOwnerComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
