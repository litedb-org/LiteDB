using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Engine
{
    /// <summary>
    /// Temporary synchronous shims for callers that still rely on the legacy blocking <see cref="ILiteEngine"/> contract.
    /// </summary>
    public static class LiteEngineSyncExtensions
    {
        [Obsolete("Use BeginTransAsync and await the result instead of blocking.")]
        public static bool BeginTrans(this ILiteEngine engine, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.BeginTransAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CommitAsync and await the result instead of blocking.")]
        public static bool Commit(this ILiteEngine engine, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.CommitAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use RollbackAsync and await the result instead of blocking.")]
        public static bool Rollback(this ILiteEngine engine, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.RollbackAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use QueryAsync and await the result instead of blocking.")]
        public static IBsonDataReader Query(this ILiteEngine engine, string collection, Query query, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.QueryAsync(collection, query, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use InsertAsync and await the result instead of blocking.")]
        public static int Insert(this ILiteEngine engine, string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.InsertAsync(collection, docs, autoId, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateAsync and await the result instead of blocking.")]
        public static int Update(this ILiteEngine engine, string collection, IEnumerable<BsonDocument> docs, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.UpdateAsync(collection, docs, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpdateManyAsync and await the result instead of blocking.")]
        public static int UpdateMany(this ILiteEngine engine, string collection, BsonExpression transform, BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.UpdateManyAsync(collection, transform, predicate, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use UpsertAsync and await the result instead of blocking.")]
        public static int Upsert(this ILiteEngine engine, string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.UpsertAsync(collection, docs, autoId, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteAsync and await the result instead of blocking.")]
        public static int Delete(this ILiteEngine engine, string collection, IEnumerable<BsonValue> ids, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.DeleteAsync(collection, ids, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DeleteManyAsync and await the result instead of blocking.")]
        public static int DeleteMany(this ILiteEngine engine, string collection, BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.DeleteManyAsync(collection, predicate, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DropCollectionAsync and await the result instead of blocking.")]
        public static bool DropCollection(this ILiteEngine engine, string name, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.DropCollectionAsync(name, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use RenameCollectionAsync and await the result instead of blocking.")]
        public static bool RenameCollection(this ILiteEngine engine, string name, string newName, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.RenameCollectionAsync(name, newName, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use EnsureIndexAsync and await the result instead of blocking.")]
        public static bool EnsureIndex(this ILiteEngine engine, string collection, string name, BsonExpression expression, bool unique, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.EnsureIndexAsync(collection, name, expression, unique, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use DropIndexAsync and await the result instead of blocking.")]
        public static bool DropIndex(this ILiteEngine engine, string collection, string name, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.DropIndexAsync(collection, name, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use PragmaAsync and await the result instead of blocking.")]
        public static BsonValue Pragma(this ILiteEngine engine, string name, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.PragmaAsync(name, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use PragmaAsync(name, value) and await the result instead of blocking.")]
        public static bool Pragma(this ILiteEngine engine, string name, BsonValue value, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.PragmaAsync(name, value, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CheckpointAsync and await the result instead of blocking.")]
        public static int Checkpoint(this ILiteEngine engine, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.CheckpointAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use RebuildAsync and await the result instead of blocking.")]
        public static long Rebuild(this ILiteEngine engine, RebuildOptions options, CancellationToken cancellationToken = default)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));

            return engine.RebuildAsync(options, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}
