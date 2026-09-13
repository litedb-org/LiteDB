using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;

namespace LiteDB
{
    public interface ILiteStorage<TFileId>
    {
        /// <summary>
        /// Find a file inside datafile and returns LiteFileInfo instance. Returns null if not found
        /// </summary>
        LiteFileInfo<TFileId> FindById(TFileId id);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IEnumerable<LiteFileInfo<TFileId>> Find(BsonExpression predicate);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, BsonDocument parameters);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, params BsonValue[] args);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        IEnumerable<LiteFileInfo<TFileId>> Find(Expression<Func<LiteFileInfo<TFileId>, bool>> predicate);

        /// <summary>
        /// Find all files inside file collections
        /// </summary>
        IEnumerable<LiteFileInfo<TFileId>> FindAll();

        /// <summary>
        /// Returns if a file exisits in database
        /// </summary>
        bool Exists(TFileId id);

        /// <summary>
        /// Open/Create new file storage and returns linked Stream to write operations.
        /// </summary>
        LiteFileStream<TFileId> OpenWrite(TFileId id, string filename, BsonDocument metadata = null);

        /// <summary>
        /// Upload a file based on stream data
        /// </summary>
        LiteFileInfo<TFileId> Upload(TFileId id, string filename, Stream stream, BsonDocument metadata = null);

        /// <summary>
        /// Upload a file based on file system data
        /// </summary>
        LiteFileInfo<TFileId> Upload(TFileId id, string filename);

        /// <summary>
        /// Update metadata on a file. File must exist.
        /// </summary>
        bool SetMetadata(TFileId id, BsonDocument metadata);

        /// <summary>
        /// Load data inside storage and returns as Stream
        /// </summary>
        LiteFileStream<TFileId> OpenRead(TFileId id);

        /// <summary>
        /// Copy all file content to a steam
        /// </summary>
        LiteFileInfo<TFileId> Download(TFileId id, Stream stream);

        /// <summary>
        /// Copy all file content to a file
        /// </summary>
        LiteFileInfo<TFileId> Download(TFileId id, string filename, bool overwritten);

        /// <summary>
        /// Delete a file inside datafile and all metadata related
        /// </summary>
        bool Delete(TFileId id);
    }
}