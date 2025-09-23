using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    public interface ILiteStorage<TFileId>
    {
        /// <summary>
        /// Find a file inside datafile and returns LiteFileInfo instance. Returns null if not found
        /// </summary>
        Task<LiteFileInfo<TFileId>> FindByIdAsync(TFileId id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(Expression<Func<LiteFileInfo<TFileId>, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find all files inside file collections
        /// </summary>
        IAsyncEnumerable<LiteFileInfo<TFileId>> FindAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns if a file exisits in database
        /// </summary>
        Task<bool> ExistsAsync(TFileId id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Open/Create new file storage and returns linked Stream to write operations.
        /// </summary>
        Task<LiteFileStream<TFileId>> OpenWriteAsync(TFileId id, string filename, BsonDocument metadata = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Upload a file based on stream data
        /// </summary>
        Task<LiteFileInfo<TFileId>> UploadAsync(TFileId id, string filename, Stream stream, BsonDocument metadata = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Upload a file based on file system data
        /// </summary>
        Task<LiteFileInfo<TFileId>> UploadAsync(TFileId id, string filename, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update metadata on a file. File must exist.
        /// </summary>
        Task<bool> SetMetadataAsync(TFileId id, BsonDocument metadata, CancellationToken cancellationToken = default);

        /// <summary>
        /// Load data inside storage and returns as Stream
        /// </summary>
        Task<LiteFileStream<TFileId>> OpenReadAsync(TFileId id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Copy all file content to a steam
        /// </summary>
        Task<LiteFileInfo<TFileId>> DownloadAsync(TFileId id, Stream stream, CancellationToken cancellationToken = default);

        /// <summary>
        /// Copy all file content to a file
        /// </summary>
        Task<LiteFileInfo<TFileId>> DownloadAsync(TFileId id, string filename, bool overwritten, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete a file inside datafile and all metadata related
        /// </summary>
        Task<bool> DeleteAsync(TFileId id, CancellationToken cancellationToken = default);
    }
}
