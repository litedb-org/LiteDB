using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Storage is a special collection to store files and streams.
    /// </summary>
    public class LiteStorage<TFileId> : ILiteStorage<TFileId>
    {
        private readonly ILiteDatabase _db;
        private readonly ILiteCollection<LiteFileInfo<TFileId>> _files;
        private readonly ILiteCollection<BsonDocument> _chunks;

        public LiteStorage(ILiteDatabase db, string filesCollection, string chunksCollection)
        {
            _db = db;
            _files = db.GetCollection<LiteFileInfo<TFileId>>(filesCollection);
            _chunks = db.GetCollection(chunksCollection);
        }

        #region Find Files

        /// <summary>
        /// Find a file inside datafile and returns LiteFileInfo instance. Returns null if not found
        /// </summary>
        public async Task<LiteFileInfo<TFileId>> FindByIdAsync(TFileId id, CancellationToken cancellationToken = default)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);

            var file = await _files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false);

            if (file == null) return null;

            file.SetReference(fileId, _files, _chunks);

            return file;
        }

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public async IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(BsonExpression predicate, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var query = _files.Query();

            if (predicate != null)
            {
                query = query.Where(predicate);
            }

            await foreach (var file in query.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                var fileId = _db.Mapper.Serialize(typeof(TFileId), file.Id);

                file.SetReference(fileId, _files, _chunks);

                yield return file;
            }
        }

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default) => this.FindAsync(BsonExpression.Create(predicate, parameters), cancellationToken);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args) => this.FindAsync(BsonExpression.Create(predicate, args), cancellationToken);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IAsyncEnumerable<LiteFileInfo<TFileId>> FindAsync(Expression<Func<LiteFileInfo<TFileId>, bool>> predicate, CancellationToken cancellationToken = default) => this.FindAsync(_db.Mapper.GetExpression(predicate), cancellationToken);

        /// <summary>
        /// Find all files inside file collections
        /// </summary>
        public IAsyncEnumerable<LiteFileInfo<TFileId>> FindAllAsync(CancellationToken cancellationToken = default) => this.FindAsync((BsonExpression)null, cancellationToken);

        /// <summary>
        /// Returns if a file exisits in database
        /// </summary>
        public Task<bool> ExistsAsync(TFileId id, CancellationToken cancellationToken = default)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);

            return _files.ExistsAsync("_id = @0", cancellationToken, fileId);
        }

        #endregion

        #region Upload

        /// <summary>
        /// Open/Create new file storage and returns linked Stream to write operations.
        /// </summary>
        public async Task<LiteFileStream<TFileId>> OpenWriteAsync(TFileId id, string filename, BsonDocument metadata = null, CancellationToken cancellationToken = default)
        {
            // get _id as BsonValue
            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);

            // checks if file exists
            var file = await this.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (file == null)
            {
                file = new LiteFileInfo<TFileId>
                {
                    Id = id,
                    Filename = Path.GetFileName(filename),
                    MimeType = MimeTypeConverter.GetMimeType(filename),
                    Metadata = metadata ?? new BsonDocument()
                };

                // set files/chunks instances
                file.SetReference(fileId, _files, _chunks);
            }
            else
            {
                // if filename/metada was changed
                file.Filename = Path.GetFileName(filename);
                file.MimeType = MimeTypeConverter.GetMimeType(filename);
                file.Metadata = metadata ?? file.Metadata;
            }

            return await file.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Upload a file based on stream data
        /// </summary>
        public async Task<LiteFileInfo<TFileId>> UploadAsync(TFileId id, string filename, Stream stream, BsonDocument metadata = null, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            await using var writer = await this.OpenWriteAsync(id, filename, metadata, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await stream.CopyToAsync(writer).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return writer.FileInfo;
        }

        /// <summary>
        /// Upload a file based on file system data
        /// </summary>
        public async Task<LiteFileInfo<TFileId>> UploadAsync(TFileId id, string filename, CancellationToken cancellationToken = default)
        {
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));

            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await this.UploadAsync(id, Path.GetFileName(filename), stream, null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Update metadata on a file. File must exist.
        /// </summary>
        public async Task<bool> SetMetadataAsync(TFileId id, BsonDocument metadata, CancellationToken cancellationToken = default)
        {
            var file = await this.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (file == null) return false;

            file.Metadata = metadata ?? new BsonDocument();

            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

            return true;
        }

        #endregion

        #region Download

        /// <summary>
        /// Load data inside storage and returns as Stream
        /// </summary>
        public async Task<LiteFileStream<TFileId>> OpenReadAsync(TFileId id, CancellationToken cancellationToken = default)
        {
            var file = await this.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (file == null) throw LiteException.FileNotFound(id.ToString());

            return await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Copy all file content to a steam
        /// </summary>
        public async Task<LiteFileInfo<TFileId>> DownloadAsync(TFileId id, Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var file = await this.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw LiteException.FileNotFound(id.ToString());

            cancellationToken.ThrowIfCancellationRequested();
            await file.CopyToAsync(stream).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return file;
        }

        /// <summary>
        /// Copy all file content to a file
        /// </summary>
        public async Task<LiteFileInfo<TFileId>> DownloadAsync(TFileId id, string filename, bool overwritten, CancellationToken cancellationToken = default)
        {
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));

            var file = await this.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw LiteException.FileNotFound(id.ToString());

            await file.SaveAsAsync(filename, overwritten, cancellationToken).ConfigureAwait(false);

            return file;
        }

        #endregion

        #region Delete

        /// <summary>
        /// Delete a file inside datafile and all metadata related
        /// </summary>
        public async Task<bool> DeleteAsync(TFileId id, CancellationToken cancellationToken = default)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            // get Id as BsonValue
            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);

            // remove file reference
            var deleted = await _files.DeleteAsync(fileId, cancellationToken).ConfigureAwait(false);

            if (deleted)
            {
                // delete all chunks
                await _chunks.DeleteManyAsync("_id BETWEEN { f: @0, n: 0} AND {f: @0, n: @1 }", cancellationToken, fileId, int.MaxValue).ConfigureAwait(false);
            }

            return deleted;
        }

        #endregion
    }
}