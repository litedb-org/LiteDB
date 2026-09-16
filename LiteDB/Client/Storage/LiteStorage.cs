using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
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
        public LiteFileInfo<TFileId> FindById(TFileId id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);

            var file = _files.FindById(fileId);

            if (file == null) return null;

            file.SetReference(fileId, _files, _chunks);

            return file;
        }

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IEnumerable<LiteFileInfo<TFileId>> Find(BsonExpression predicate)
        {
            var query = _files.Query();

            if (predicate != null)
            {
                query = query.Where(predicate);
            }

            foreach (var file in query.ToEnumerable())
            {
                var fileId = _db.Mapper.Serialize(typeof(TFileId), file.Id);

                file.SetReference(fileId, _files, _chunks);

                yield return file;
            }
        }

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, BsonDocument parameters) => this.Find(BsonExpression.Create(predicate, parameters));

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, params BsonValue[] args) => this.Find(BsonExpression.Create(predicate, args));

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        public IEnumerable<LiteFileInfo<TFileId>> Find(Expression<Func<LiteFileInfo<TFileId>, bool>> predicate) => this.Find(_db.Mapper.GetExpression(predicate));

        /// <summary>
        /// Find all files inside file collections
        /// </summary>
        public IEnumerable<LiteFileInfo<TFileId>> FindAll() => this.Find((BsonExpression)null);

        /// <summary>
        /// Returns if a file exisits in database
        /// </summary>
        public bool Exists(TFileId id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);

            return _files.Exists("_id = @0", fileId);
        }

        #endregion

        #region Upload

        /// <summary>
        /// Open/Create new file storage and returns linked Stream to write operations.
        /// </summary>
        public LiteFileStream<TFileId> OpenWrite(TFileId id, string filename, BsonDocument metadata = null)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            // get _id as BsonValue
            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);
            if (fileId == null || fileId.IsNull) throw new ArgumentNullException(nameof(id));

            var name = Path.GetFileName(filename);
            var mimeType = MimeTypeConverter.GetMimeType(filename);

            // Caller transactions must take the same file-before-chunk locks as
            // Upload and Delete. Update acquires a lock without creating a collection.
            _files.Update(Enumerable.Empty<LiteFileInfo<TFileId>>());

            // checks if file exists
            var file = this.FindById(id);

            if (file == null)
            {
                file = new LiteFileInfo<TFileId>
                {
                    Id = id,
                    Filename = name,
                    MimeType = mimeType,
                    Metadata = metadata ?? new BsonDocument()
                };

                // set files/chunks instances
                file.SetReference(fileId, _files, _chunks);
            }
            else
            {
                // if filename/metada was changed
                file.Filename = name;
                file.MimeType = mimeType;
                file.Metadata = metadata ?? file.Metadata;
            }

            return file.OpenWrite();
        }

        /// <summary>
        /// Upload a file based on stream data
        /// Content and metadata are committed together. Failure rolls back the active transaction.
        /// </summary>
        public LiteFileInfo<TFileId> Upload(TFileId id, string filename, Stream stream, BsonDocument metadata = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var ownsTransaction = _db.BeginTrans();
            LiteFileStream<TFileId> writer = null;
            try
            {
                writer = this.OpenWrite(id, filename, metadata);
                stream.CopyTo(writer);
                writer.Dispose();
                if (ownsTransaction) _db.Commit();
                return writer.FileInfo;
            }
            catch
            {
                writer?.Abort();
                // A failed engine operation may already have aborted or closed it.
                // Preserve the upload error if rollback reports that terminal state.
                try { _db.Rollback(); }
                catch { }
                throw;
            }
        }

        /// <summary>
        /// Upload a file based on file system data
        /// </summary>
        public LiteFileInfo<TFileId> Upload(TFileId id, string filename)
        {
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));

            using (var stream = File.OpenRead(filename))
            {
                return this.Upload(id, Path.GetFileName(filename), stream);
            }
        }

        /// <summary>
        /// Update metadata on a file. File must exist.
        /// </summary>
        public bool SetMetadata(TFileId id, BsonDocument metadata)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);
            if (fileId == null || fileId.IsNull) throw new ArgumentNullException(nameof(id));
            if (!_files.Exists("_id = @0", fileId)) return false;

            // Update enumerates after acquiring the file collection write lock.
            // Read and map the current record inside that same transaction so
            // serializer hooks remain intact and cannot overwrite a newer upload.
            IEnumerable<LiteFileInfo<TFileId>> update()
            {
                var file = this.FindById(id);
                if (file == null) yield break;
                file.Metadata = metadata ?? new BsonDocument();
                yield return file;
            }

            return _files.Update(update()) > 0;
        }

        #endregion

        #region Download

        /// <summary>
        /// Load data inside storage and returns as Stream
        /// </summary>
        public LiteFileStream<TFileId> OpenRead(TFileId id)
        {
            var file = this.FindById(id);

            if (file == null) throw LiteException.FileNotFound(id.ToString());

            return file.OpenRead();
        }

        /// <summary>
        /// Copy all file content to a steam
        /// </summary>
        public LiteFileInfo<TFileId> Download(TFileId id, Stream stream)
        {
            var file = this.FindById(id) ?? throw LiteException.FileNotFound(id.ToString());

            file.CopyTo(stream);

            return file;
        }

        /// <summary>
        /// Copy all file content to a file
        /// </summary>
        public LiteFileInfo<TFileId> Download(TFileId id, string filename, bool overwritten)
        {
            var file = this.FindById(id) ?? throw LiteException.FileNotFound(id.ToString());

            file.SaveAs(filename, overwritten);

            return file;
        }

        #endregion

        #region Delete

        /// <summary>
        /// Delete file content and metadata together in the active write transaction.
        /// </summary>
        public bool Delete(TFileId id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            // get Id as BsonValue
            var fileId = _db.Mapper.Serialize(typeof(TFileId), id);
            if (fileId == null || fileId.IsNull) throw new ArgumentNullException(nameof(id));

            // An absent record can belong to an unfinished incremental OpenWrite.
            // Returning here preserves its chunks and does not create a collection.
            if (!_files.Exists("_id = @0", fileId)) return false;

            return this.WriteWithFileLock(() =>
            {
                var deleted = _files.Delete(fileId);
                if (deleted)
                {
                    _chunks.DeleteMany("_id BETWEEN { f: @0, n: 0} AND {f: @0, n: @1 }", fileId, int.MaxValue);
                }
                return deleted;
            });
        }

        private TResult WriteWithFileLock<TResult>(Func<TResult> write)
        {
            var result = default(TResult);
            IEnumerable<LiteFileInfo<TFileId>> batch()
            {
                // Update enumerates after acquiring its write lock and transaction.
                // Yield no documents: nested storage writes form the atomic batch.
                // Forwarding engines retain the normal single-pass enumeration contract.
                result = write();
                yield break;
            }
            _files.Update(batch());
            return result;
        }

        #endregion
    }
}
