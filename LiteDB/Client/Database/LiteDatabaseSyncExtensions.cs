using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>
    /// Temporary synchronous shims that bridge existing call sites to the new asynchronous-first database contract.
    /// </summary>
    public static class LiteDatabaseSyncExtensions
    {
        [Obsolete("Use BeginTransAsync and await the result instead of blocking.")]
        public static bool BeginTrans(this ILiteDatabase database, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.BeginTransAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CommitAsync and await the result instead of blocking.")]
        public static bool Commit(this ILiteDatabase database, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.CommitAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use RollbackAsync and await the result instead of blocking.")]
        public static bool Rollback(this ILiteDatabase database, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.RollbackAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExecuteAsync(TextReader, ...) and await the result instead of blocking.")]
        public static IBsonDataReader Execute(this ILiteDatabase database, TextReader commandReader, BsonDocument parameters = null, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.ExecuteAsync(commandReader, parameters, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExecuteAsync(string, BsonDocument, ...) and await the result instead of blocking.")]
        public static IBsonDataReader Execute(this ILiteDatabase database, string command, BsonDocument parameters = null, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.ExecuteAsync(command, parameters, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExecuteAsync(string, params BsonValue[]) and await the result instead of blocking.")]
        public static IBsonDataReader Execute(this ILiteDatabase database, string command, CancellationToken cancellationToken, params BsonValue[] args)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.ExecuteAsync(command, cancellationToken, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use ExecuteAsync(string, params BsonValue[]) and await the result instead of blocking.")]
        public static IBsonDataReader Execute(this ILiteDatabase database, string command, params BsonValue[] args)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.ExecuteAsync(command, args).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use CheckpointAsync and await the result instead of blocking.")]
        public static void Checkpoint(this ILiteDatabase database, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            database.CheckpointAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        [Obsolete("Use RebuildAsync and await the result instead of blocking.")]
        public static long Rebuild(this ILiteDatabase database, RebuildOptions options = null, CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            return database.RebuildAsync(options, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}
