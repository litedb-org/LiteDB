using System;
using System.Collections.Generic;

namespace LiteDB
{
    /// <summary>The outcome of one explicitly owned transaction, independent of resource cleanup.</summary>
    public enum LiteTransactionState
    {
        /// <summary>The transaction accepts operations and has not completed.</summary>
        Active,

        /// <summary>The commit was durably published under the configured durability contract.</summary>
        Committed,

        /// <summary>The transaction was rolled back; none of its writes are visible.</summary>
        RolledBack,

        /// <summary>An operation or rollback failed; the transaction cannot be used and did not commit.</summary>
        Failed,

        /// <summary>Commit failed after it may have published; the outcome must be established by reading.</summary>
        Indeterminate
    }

    /// <summary>
    /// A synchronous transaction that supports sequential thread handoff. Concurrent or reentrant
    /// use of one handle is rejected. Collections, queries and readers obtained from this handle
    /// always use this transaction and never fall back to automatic transactions; ordinary
    /// database collections never enlist in it.
    /// </summary>
    public interface ILiteTransaction : IDisposable
    {
        /// <summary>The transaction's outcome.</summary>
        LiteTransactionState State { get; }

        /// <summary>Get a typed collection bound to this transaction.</summary>
        ILiteCollection<T> GetCollection<T>(string name = null, BsonAutoId autoId = BsonAutoId.ObjectId);

        /// <summary>Get a BSON collection bound to this transaction.</summary>
        ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId);

        /// <summary>User collection names visible to this transaction, including ones it created.</summary>
        IEnumerable<string> GetCollectionNames();

        /// <summary>Whether a user collection is visible to this transaction.</summary>
        bool CollectionExists(string name);

        /// <summary>Commit the transaction. Close its bound readers first.</summary>
        void Commit();

        /// <summary>Roll back the transaction.</summary>
        void Rollback();
    }

    /// <summary>Optional capability; existing <see cref="ILiteDatabase"/> implementations need not implement it.</summary>
    public interface ILiteTransactionProvider
    {
        /// <summary>Start an independent transaction handle.</summary>
        ILiteTransaction BeginTransaction();
    }

    /// <summary>Transaction handle access for any <see cref="ILiteDatabase"/>.</summary>
    public static class LiteTransactionExtensions
    {
        /// <summary>
        /// Creates an independent handle without enlisting ordinary database collections.
        /// Throws <see cref="NotSupportedException"/>, before any side effect, when the database
        /// does not implement <see cref="ILiteTransactionProvider"/>.
        /// </summary>
        public static ILiteTransaction BeginTransaction(this ILiteDatabase database)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (database is ILiteTransactionProvider provider) return provider.BeginTransaction();
            throw new NotSupportedException("This database provider does not support thread-independent transaction handles.");
        }
    }
}
