using System;
using System.Collections.Generic;
using LiteDB.Vector;

namespace LiteDB.Engine
{
    /// <summary>
    /// Defines the core database engine interface for low-level database operations.
    /// </summary>
    /// <remarks>
    /// <see cref="ILiteEngine"/> provides the fundamental operations for managing collections, documents, indexes, and transactions.
    /// This interface is implemented by <see cref="LiteEngine"/> for direct access and <see cref="SharedEngine"/> for shared/multi-process access.
    /// </remarks>
    public interface ILiteEngine : IDisposable
    {
        /// <summary>
        /// Performs a database checkpoint, copying all committed transactions from the log file to the data file.
        /// </summary>
        /// <returns>The number of pages copied during the checkpoint operation.</returns>
        int Checkpoint();

        /// <summary>
        /// Rebuilds the entire database to remove unused pages and reduce the data file size.
        /// </summary>
        /// <param name="options">The rebuild options specifying how the rebuild should be performed.</param>
        /// <returns>The number of bytes reduced from the data file.</returns>
        long Rebuild(RebuildOptions options);

        /// <summary>
        /// Begins a new transaction on the current thread.
        /// </summary>
        /// <returns><see langword="true"/> if a new transaction was created; <see langword="false"/> if the current thread already has an active transaction.</returns>
        bool BeginTrans();

        /// <summary>
        /// Commits the current transaction, persisting all changes to the database.
        /// </summary>
        /// <returns><see langword="true"/> if the transaction was committed; <see langword="false"/> if no active transaction exists.</returns>
        bool Commit();

        /// <summary>
        /// Rolls back the current transaction, discarding all uncommitted changes.
        /// </summary>
        /// <returns><see langword="true"/> if the transaction was rolled back; <see langword="false"/> if no active transaction exists.</returns>
        bool Rollback();

        /// <summary>
        /// Executes a query against a collection and returns the results as a data reader.
        /// </summary>
        /// <param name="collection">The collection name to query.</param>
        /// <param name="query">The query object specifying filter, sort, and projection.</param>
        /// <returns>An <see cref="IBsonDataReader"/> containing the query results.</returns>
        IBsonDataReader Query(string collection, Query query);

        /// <summary>
        /// Inserts one or more documents into a collection.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="docs">The documents to insert.</param>
        /// <param name="autoId">The auto-ID generation strategy for documents without an <c>_id</c> field.</param>
        /// <returns>The number of documents inserted.</returns>
        int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId);

        /// <summary>
        /// Updates one or more documents in a collection based on their <c>_id</c> values.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="docs">The documents to update. Each document must have an <c>_id</c> field matching an existing document.</param>
        /// <returns>The number of documents updated.</returns>
        int Update(string collection, IEnumerable<BsonDocument> docs);

        /// <summary>
        /// Updates multiple documents in a collection that match a predicate by applying a transformation expression.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="transform">The expression that defines how to transform matching documents.</param>
        /// <param name="predicate">The expression that filters which documents to update.</param>
        /// <returns>The number of documents updated.</returns>
        int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate);

        /// <summary>
        /// Inserts or updates one or more documents in a collection. Documents with existing <c>_id</c> values are updated; new documents are inserted.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="docs">The documents to upsert.</param>
        /// <param name="autoId">The auto-ID generation strategy for new documents without an <c>_id</c> field.</param>
        /// <returns>The number of documents inserted or updated.</returns>
        int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId);

        /// <summary>
        /// Deletes one or more documents from a collection by their <c>_id</c> values.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="ids">The <c>_id</c> values of the documents to delete.</param>
        /// <returns>The number of documents deleted.</returns>
        int Delete(string collection, IEnumerable<BsonValue> ids);

        /// <summary>
        /// Deletes multiple documents from a collection that match a predicate expression.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="predicate">The expression that filters which documents to delete.</param>
        /// <returns>The number of documents deleted.</returns>
        int DeleteMany(string collection, BsonExpression predicate);

        /// <summary>
        /// Drops a collection, removing all documents and indexes.
        /// </summary>
        /// <param name="name">The collection name to drop.</param>
        /// <returns><see langword="true"/> if the collection was dropped; <see langword="false"/> if the collection does not exist.</returns>
        bool DropCollection(string name);

        /// <summary>
        /// Renames a collection.
        /// </summary>
        /// <param name="name">The current collection name.</param>
        /// <param name="newName">The new collection name.</param>
        /// <returns><see langword="true"/> if the collection was renamed; <see langword="false"/> if the collection does not exist or the new name already exists.</returns>
        bool RenameCollection(string name, string newName);

        /// <summary>
        /// Ensures an index exists on a collection.
        /// <para>Creates the index if it does not exist.</para>
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="name">The index name.</param>
        /// <param name="expression">The expression defining the index key.</param>
        /// <param name="unique">If <see langword="true"/>, the index enforces uniqueness.</param>
        /// <returns><see langword="true"/> if a new index was created; <see langword="false"/> if the index already exists.</returns>
        bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique);

        /// <summary>
        /// Ensures a vector index exists on a collection for similarity search operations.
        /// <para>Creates the index if it does not exist.</para>
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="name">The index name.</param>
        /// <param name="expression">The expression defining the vector field to index.</param>
        /// <param name="options">The vector index options specifying dimensions and distance metric.</param>
        /// <returns><see langword="true"/> if a new vector index was created; <see langword="false"/> if the index already exists.</returns>
        bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options);

        /// <summary>
        /// Drops an index from a collection.
        /// </summary>
        /// <param name="collection">The collection name.</param>
        /// <param name="name">The index name to drop.</param>
        /// <returns><see langword="true"/> if the index was dropped; <see langword="false"/> if the index does not exist.</returns>
        bool DropIndex(string collection, string name);

        /// <summary>
        /// Gets the value of an internal engine variable (pragma).
        /// </summary>
        /// <param name="name">The pragma name.</param>
        /// <returns>The current value of the pragma as a <see cref="BsonValue"/>.</returns>
        BsonValue Pragma(string name);

        /// <summary>
        /// Sets the value of an internal engine variable (pragma).
        /// </summary>
        /// <param name="name">The pragma name.</param>
        /// <param name="value">The new value for the pragma.</param>
        /// <returns><see langword="true"/> if the pragma was successfully set; <see langword="false"/> otherwise.</returns>
        bool Pragma(string name, BsonValue value);
    }
}