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
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileInfo<TFileId> FindById(TFileId id);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        IEnumerable<LiteFileInfo<TFileId>> Find(BsonExpression predicate);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, BsonDocument parameters);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, params BsonValue[] args);

        /// <summary>
        /// Find all files that match with predicate expression.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        IEnumerable<LiteFileInfo<TFileId>> Find(Expression<Func<LiteFileInfo<TFileId>, bool>> predicate);

        /// <summary>
        /// Find all files inside file collections
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        IEnumerable<LiteFileInfo<TFileId>> FindAll();

        /// <summary>
        /// Returns if a file exisits in database
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        bool Exists(TFileId id);

        /// <summary>
        /// Open/Create new file storage and returns linked Stream to write operations.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileStream<TFileId> OpenWrite(TFileId id, string filename, BsonDocument metadata = null);

        /// <summary>
        /// Upload a file based on stream data
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileInfo<TFileId> Upload(TFileId id, string filename, Stream stream, BsonDocument metadata = null);

        /// <summary>
        /// Upload a file based on file system data
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileInfo<TFileId> Upload(TFileId id, string filename);

        /// <summary>
        /// Update metadata on a file. File must exist.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        bool SetMetadata(TFileId id, BsonDocument metadata);

        /// <summary>
        /// Load data inside storage and returns as Stream
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileStream<TFileId> OpenRead(TFileId id);

        /// <summary>
        /// Copy all file content to a steam
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileInfo<TFileId> Download(TFileId id, Stream stream);

        /// <summary>
        /// Copy all file content to a file
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        LiteFileInfo<TFileId> Download(TFileId id, string filename, bool overwritten);

        /// <summary>
        /// Delete a file inside datafile and all metadata related
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        bool Delete(TFileId id);
    }
}
