using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    /// <summary>
    /// Represents a typed collection of documents in a LiteDB database, providing CRUD operations, querying, and indexing capabilities.
    /// </summary>
    /// <typeparam name="T">The entity type for documents in this collection. Can be a POCO class or <see cref="BsonDocument"/>.</typeparam>
    /// <remarks>
    /// <para>
    /// <see cref="ILiteCollection{T}"/> is the primary interface for working with documents in LiteDB. It supports strongly-typed
    /// operations for POCOs and dynamic operations for <see cref="BsonDocument"/>.
    /// </para>
    /// <para>
    /// Collections are created automatically when first accessed. All operations are ACID-compliant when used within transactions.
    /// </para>
    /// </remarks>
    public interface ILiteCollection<T>
    {
        /// <summary>
        /// Gets the collection name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the auto-ID generation strategy for this collection.
        /// </summary>
        BsonAutoId AutoId { get; }

        /// <summary>
        /// Gets the entity mapper for this collection. Returns <see langword="null"/> if the collection uses <see cref="BsonDocument"/> type.
        /// </summary>
        EntityMapper EntityMapper { get; }

        /// <summary>
        /// Configures the collection to load DbRef documents using a LINQ expression for related documents.
        /// </summary>
        /// <typeparam name="K">The type of the related document or collection.</typeparam>
        /// <param name="keySelector">An expression selecting the DbRef field to load.</param>
        /// <returns>A new collection instance with the include action configured.</returns>
        /// <remarks>
        /// Include actions are applied to documents returned by <see cref="Find(BsonExpression, int, int)"/>, <see cref="FindById"/>, 
        /// <see cref="FindOne(BsonExpression)"/>, and <see cref="FindAll"/>.
        /// <para>Multiple includes can be chained.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var customers = db.GetCollection&lt;Customer&gt;();
        /// var results = customers
        ///     .Include(x => x.Address)
        ///     .Find(x => x.Active);
        /// </code>
        /// </example>
        ILiteCollection<T> Include<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Configures the collection to load DbRef documents using a BSON expression for related documents.
        /// </summary>
        /// <param name="keySelector">A BSON expression selecting the DbRef field to load.</param>
        /// <returns>A new collection instance with the include action configured.</returns>
        /// <remarks>
        /// Include actions are applied to documents returned by find operations.
        /// <para>Use this overload for dynamic field selection.</para>
        /// </remarks>
        ILiteCollection<T> Include(BsonExpression keySelector);

        /// <summary>
        /// Inserts a new document or updates an existing document based on the <c>_id</c> field.
        /// </summary>
        /// <param name="entity">The document to insert or update.</param>
        /// <returns><see langword="true"/> if the document was inserted; <see langword="false"/> if it was updated.</returns>
        /// <example>
        /// <code>
        /// var customer = new Customer { Id = 1, Name = "John" };
        /// bool inserted = col.Upsert(customer); // true on first call, false on subsequent calls
        /// </code>
        /// </example>
        bool Upsert(T entity);

        /// <summary>
        /// Inserts new documents or updates existing documents for all entities in the collection.
        /// </summary>
        /// <param name="entities">The documents to insert or update.</param>
        /// <returns>The total number of documents inserted (the updated documents are not counted).</returns>
        int Upsert(IEnumerable<T> entities);

        /// <summary>
        /// Inserts a new document with the specified ID or updates the existing document with that ID.
        /// </summary>
        /// <param name="id">The <c>_id</c> value for the document.</param>
        /// <param name="entity">The document to insert or update.</param>
        /// <returns><see langword="true"/> if the document was inserted; <see langword="false"/> if it was updated.</returns>
        bool Upsert(BsonValue id, T entity);

        /// <summary>
        /// Updates an existing document in the collection based on its <c>_id</c> field.
        /// </summary>
        /// <param name="entity">The document to update. Must have an <c>_id</c> field.</param>
        /// <returns><see langword="true"/> if the document was found and updated; <see langword="false"/> if not found.</returns>
        /// <example>
        /// <code>
        /// customer.Name = "John Doe";
        /// bool updated = col.Update(customer); // false if customer doesn't exist
        /// </code>
        /// </example>
        bool Update(T entity);

        /// <summary>
        /// Updates an existing document with the specified ID.
        /// </summary>
        /// <param name="id">The <c>_id</c> of the document to update.</param>
        /// <param name="entity">The replacement document.</param>
        /// <returns><see langword="true"/> if the document was found and updated; <see langword="false"/> if not found.</returns>
        bool Update(BsonValue id, T entity);

        /// <summary>
        /// Updates multiple existing documents in the collection.
        /// </summary>
        /// <param name="entities">The documents to update.</param>
        /// <returns>The number of documents successfully updated.</returns>
        int Update(IEnumerable<T> entities);

        /// <summary>
        /// Updates multiple documents using a transformation expression and a filter predicate.
        /// </summary>
        /// <param name="transform">The BSON expression defining how to transform matching documents.</param>
        /// <param name="predicate">The BSON expression filtering which documents to update.</param>
        /// <returns>The number of documents updated.</returns>
        /// <example>
        /// <code>
        /// // Update all active customers to uppercase names
        /// int count = col.UpdateMany(
        ///     "{ Name: UPPER($.Name) }", 
        ///     "Active = true"
        /// );
        /// </code>
        /// </example>
        int UpdateMany(BsonExpression transform, BsonExpression predicate);

        /// <summary>
        /// Updates multiple documents using a LINQ transformation expression and filter predicate.
        /// </summary>
        /// <param name="extend">A LINQ expression defining how to transform matching documents.</param>
        /// <param name="predicate">A LINQ expression filtering which documents to update.</param>
        /// <returns>The number of documents updated.</returns>
        /// <example>
        /// <code>
        /// int count = col.UpdateMany(
        ///     x => new Customer { Name = x.Name.ToUpper(), Salary = 100 },
        ///     x => x.Active
        /// );
        /// </code>
        /// </example>
        int UpdateMany(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Inserts a new document into the collection. The document must not already exist.
        /// </summary>
        /// <param name="entity">The document to insert.</param>
        /// <returns>The <c>_id</c> of the inserted document.</returns>
        /// <exception cref="LiteException">Thrown when a document with the same <c>_id</c> already exists.</exception>
        /// <example>
        /// <code>
        /// var customer = new Customer { Name = "John" };
        /// var id = col.Insert(customer);
        /// </code>
        /// </example>
        BsonValue Insert(T entity);

        /// <summary>
        /// Inserts a new document with the specified <c>_id</c> value.
        /// </summary>
        /// <param name="id">The <c>_id</c> value for the new document.</param>
        /// <param name="entity">The document to insert.</param>
        /// <exception cref="LiteException">Thrown when a document with the specified <c>_id</c> already exists.</exception>
        void Insert(BsonValue id, T entity);

        /// <summary>
        /// Inserts multiple new documents into the collection.
        /// </summary>
        /// <param name="entities">The documents to insert.</param>
        /// <returns>The number of documents inserted.</returns>
        int Insert(IEnumerable<T> entities);

        /// <summary>
        /// Performs a bulk insert operation for large numbers of documents with batching.
        /// </summary>
        /// <param name="entities">The documents to insert.</param>
        /// <param name="batchSize">The number of documents to commit per batch.</param>
        /// <returns>The total number of documents inserted.</returns>
        /// <remarks>
        /// Bulk insert is optimized for inserting large numbers of documents by committing in batches,
        /// improving performance for mass data imports.
        /// </remarks>
        int InsertBulk(IEnumerable<T> entities, int batchSize = 5000);

        /// <summary>
        /// Creates an index on the collection if it does not already exist.
        /// </summary>
        /// <param name="name">The unique name for the index within this collection.</param>
        /// <param name="expression">The BSON expression defining the index key.</param>
        /// <param name="unique">If <see langword="true"/>, creates a unique index that prevents duplicate values.</param>
        /// <returns><see langword="true"/> if a new index was created; <see langword="false"/> if the index already exists.</returns>
        /// <example>
        /// <code>
        /// col.EnsureIndex("idx_email", "$.Email", unique: true);
        /// </code>
        /// </example>
        bool EnsureIndex(string name, BsonExpression expression, bool unique = false);

        /// <summary>
        /// Creates an index on the collection using an expression, with an auto-generated index name.
        /// </summary>
        /// <param name="expression">The BSON expression defining the index key.</param>
        /// <param name="unique">If <see langword="true"/>, creates a unique index.</param>
        /// <returns><see langword="true"/> if a new index was created; <see langword="false"/> if the index already exists.</returns>
        bool EnsureIndex(BsonExpression expression, bool unique = false);

        /// <summary>
        /// Creates an index using a LINQ expression, with an auto-generated index name.
        /// </summary>
        /// <typeparam name="K">The type of the indexed field.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to index.</param>
        /// <param name="unique">If <see langword="true"/>, creates a unique index.</param>
        /// <returns><see langword="true"/> if a new index was created; <see langword="false"/> if the index already exists.</returns>
        /// <example>
        /// <code>
        /// col.EnsureIndex(x => x.Email, unique: true);
        /// </code>
        /// </example>
        bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, bool unique = false);

        /// <summary>
        /// Creates an index using a LINQ expression with a custom index name.
        /// </summary>
        /// <typeparam name="K">The type of the indexed field.</typeparam>
        /// <param name="name">The unique name for the index within this collection.</param>
        /// <param name="keySelector">A LINQ expression selecting the field to index.</param>
        /// <param name="unique">If <see langword="true"/>, creates a unique index.</param>
        /// <returns><see langword="true"/> if a new index was created; <see langword="false"/> if the index already exists.</returns>
        bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false);

        /// <summary>
        /// Drops an index from the collection, releasing the index slot.
        /// </summary>
        /// <param name="name">The name of the index to drop.</param>
        /// <returns><see langword="true"/> if the index was dropped; <see langword="false"/> if the index does not exist.</returns>
        bool DropIndex(string name);

        /// <summary>
        /// Returns a new queryable interface for building complex queries with fluent syntax.
        /// </summary>
        /// <returns>An <see cref="ILiteQueryable{T}"/> instance for building queries.</returns>
        /// <example>
        /// <code>
        /// var results = col.Query()
        ///     .Where(x => x.Age > 18)
        ///     .OrderBy(x => x.Name)
        ///     .Limit(10)
        ///     .ToList();
        /// </code>
        /// </example>
        ILiteQueryable<T> Query();

        /// <summary>
        /// Finds documents matching a BSON expression predicate.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents.</param>
        /// <param name="skip">The number of documents to skip.</param>
        /// <param name="limit">The maximum number of documents to return. Default is <see cref="int.MaxValue"/>.</param>
        /// <returns>An enumerable collection of matching documents.</returns>
        IEnumerable<T> Find(BsonExpression predicate, int skip = 0, int limit = int.MaxValue);

        /// <summary>
        /// Finds documents matching a query definition.
        /// </summary>
        /// <param name="query">The query object defining filter, sort, and projection.</param>
        /// <param name="skip">The number of documents to skip.</param>
        /// <param name="limit">The maximum number of documents to return. Default is <see cref="int.MaxValue"/>.</param>
        /// <returns>An enumerable collection of matching documents.</returns>
        IEnumerable<T> Find(Query query, int skip = 0, int limit = int.MaxValue);

        /// <summary>
        /// Finds documents matching a LINQ expression predicate.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents.</param>
        /// <param name="skip">The number of documents to skip. Default is 0.</param>
        /// <param name="limit">The maximum number of documents to return.</param>
        /// <returns>An enumerable collection of matching documents.</returns>
        /// <example>
        /// <code>
        /// var adults = col.Find(x => x.Age >= 18, skip: 10, limit: 20);
        /// </code>
        /// </example>
        IEnumerable<T> Find(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue);

        /// <summary>
        /// Finds a document by its <c>_id</c> value.
        /// </summary>
        /// <param name="id">The <c>_id</c> of the document to find.</param>
        /// <returns>The document if found; otherwise, <see langword="null"/>.</returns>
        /// <example>
        /// <code>
        /// var customer = col.FindById(1);
        /// </code>
        /// </example>
        T FindById(BsonValue id);

        /// <summary>
        /// Finds the first document matching a BSON expression predicate.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents.</param>
        /// <returns>The first matching document, or <see langword="null"/> if no match is found.</returns>
        T FindOne(BsonExpression predicate);

        /// <summary>
        /// Finds the first document matching a BSON expression with named parameters.
        /// </summary>
        /// <param name="predicate">The BSON expression string to filter documents.</param>
        /// <param name="parameters">A document containing named parameter values.</param>
        /// <returns>The first matching document, or <see langword="null"/> if no match is found.</returns>
        T FindOne(string predicate, BsonDocument parameters);

        /// <summary>
        /// Finds the first document matching a BSON expression with positional parameters.
        /// </summary>
        /// <param name="predicate">The BSON expression string using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values.</param>
        /// <returns>The first matching document, or <see langword="null"/> if no match is found.</returns>
        T FindOne(BsonExpression predicate, params BsonValue[] args);

        /// <summary>
        /// Finds the first document matching a LINQ expression predicate.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents.</param>
        /// <returns>The first matching document, or <see langword="null"/> if no match is found.</returns>
        /// <example>
        /// <code>
        /// var customer = col.FindOne(x => x.Email == "john@example.com");
        /// </code>
        /// </example>
        T FindOne(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Finds the first document matching a query definition.
        /// </summary>
        /// <param name="query">The query object defining the filter.</param>
        /// <returns>The first matching document, or <see langword="null"/> if no match is found.</returns>
        T FindOne(Query query);

        /// <summary>
        /// Returns all documents in the collection ordered by the <c>_id</c> index.
        /// </summary>
        /// <returns>An enumerable collection of all documents.</returns>
        IEnumerable<T> FindAll();

        /// <summary>
        /// Deletes a single document by its <c>_id</c> value.
        /// </summary>
        /// <param name="id">The <c>_id</c> of the document to delete.</param>
        /// <returns><see langword="true"/> if the document was deleted; <see langword="false"/> if not found.</returns>
        /// <example>
        /// <code>
        /// bool deleted = col.Delete(1);
        /// </code>
        /// </example>
        bool Delete(BsonValue id);

        /// <summary>
        /// Deletes all documents in the collection.
        /// </summary>
        /// <returns>The number of documents deleted.</returns>
        int DeleteAll();

        /// <summary>
        /// Deletes all documents matching a BSON expression predicate.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents for deletion.</param>
        /// <returns>The number of documents deleted.</returns>
        int DeleteMany(BsonExpression predicate);

        /// <summary>
        /// Deletes all documents matching a BSON expression with named parameters.
        /// </summary>
        /// <param name="predicate">The BSON expression string to filter documents.</param>
        /// <param name="parameters">A document containing named parameter values.</param>
        /// <returns>The number of documents deleted.</returns>
        int DeleteMany(string predicate, BsonDocument parameters);

        /// <summary>
        /// Deletes all documents matching a BSON expression with positional parameters.
        /// </summary>
        /// <param name="predicate">The BSON expression string using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values.</param>
        /// <returns>The number of documents deleted.</returns>
        int DeleteMany(string predicate, params BsonValue[] args);

        /// <summary>
        /// Deletes all documents matching a LINQ expression predicate.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents for deletion.</param>
        /// <returns>The number of documents deleted.</returns>
        /// <example>
        /// <code>
        /// int deleted = col.DeleteMany(x => x.Age &lt; 18);
        /// </code>
        /// </example>
        int DeleteMany(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Gets the total number of documents in the collection without deserializing documents.
        /// </summary>
        /// <returns>The document count.</returns>
        /// <remarks>
        /// This method uses internal properties and does not deserialize documents, making it very efficient.
        /// </remarks>
        int Count();

        /// <summary>
        /// Counts documents matching a BSON expression predicate without deserializing documents.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses indexes and does not deserialize documents, making it very efficient.
        /// Ensure appropriate indexes exist for best performance.
        /// </remarks>
        int Count(BsonExpression predicate);

        /// <summary>
        /// Counts documents matching a BSON expression with named parameters without deserializing documents.
        /// </summary>
        /// <param name="predicate">The BSON expression string to filter documents.</param>
        /// <param name="parameters">A document containing named parameter values.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses indexes and does not deserialize documents, making it very efficient.
        /// Ensure appropriate indexes exist for best performance.
        /// </remarks>
        int Count(string predicate, BsonDocument parameters);

        /// <summary>
        /// Counts documents matching a BSON expression with positional parameters without deserializing documents.
        /// </summary>
        /// <param name="predicate">The BSON expression string using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses indexes and does not deserialize documents, making it very efficient.
        /// Ensure appropriate indexes exist for best performance.
        /// </remarks>
        int Count(string predicate, params BsonValue[] args);

        /// <summary>
        /// Counts documents matching a LINQ expression predicate without deserializing documents.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses indexes and does not deserialize documents, making it very efficient.
        /// Ensure appropriate indexes exist for best performance.
        /// </remarks>
        int Count(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Counts documents matching a query without deserializing documents.
        /// </summary>
        /// <param name="query">The query object defining the filter.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses indexes and does not deserialize documents, making it very efficient.
        /// Ensure appropriate indexes exist for best performance.
        /// </remarks>
        int Count(Query query);

        /// <summary>
        /// Gets the total number of documents in the collection as a <see cref="long"/>.
        /// </summary>
        /// <returns>The document count.</returns>
        long LongCount();

        /// <summary>
        /// Counts documents matching a BSON expression predicate without deserializing documents, returning a <see cref="long"/>.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses internal properties and does not deserialize documents, making it very efficient.
        /// </remarks>
        long LongCount(BsonExpression predicate);

        /// <summary>
        /// Counts documents matching a BSON expression with named parameters without deserializing documents, returning a <see cref="long"/>.
        /// </summary>
        /// <param name="predicate">The BSON expression string to filter documents.</param>
        /// <param name="parameters">A document containing named parameter values.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses internal properties and does not deserialize documents, making it very efficient.
        /// </remarks>
        long LongCount(string predicate, BsonDocument parameters);

        /// <summary>
        /// Counts documents matching a BSON expression with positional parameters without deserializing documents, returning a <see cref="long"/>.
        /// </summary>
        /// <param name="predicate">The BSON expression string using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses internal properties and does not deserialize documents, making it very efficient.
        /// </remarks>
        long LongCount(string predicate, params BsonValue[] args);

        /// <summary>
        /// Counts documents matching a LINQ expression predicate without deserializing documents, returning a <see cref="long"/>.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses internal properties and does not deserialize documents, making it very efficient.
        /// </remarks>
        long LongCount(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Counts documents matching a query without deserializing documents, returning a <see cref="long"/>.
        /// </summary>
        /// <param name="query">The query object defining the filter.</param>
        /// <returns>The count of matching documents.</returns>
        /// <remarks>
        /// This method uses internal properties and does not deserialize documents, making it very efficient.
        /// </remarks>
        long LongCount(Query query);

        /// <summary>
        /// Checks if any document matches a BSON expression predicate without deserializing documents.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents.</param>
        /// <returns><see langword="true"/> if at least one matching document exists; otherwise, <see langword="false"/>.</returns>
        bool Exists(BsonExpression predicate);

        /// <summary>
        /// Checks if any document matches a BSON expression with named parameters without deserializing documents.
        /// </summary>
        /// <param name="predicate">The BSON expression string to filter documents.</param>
        /// <param name="parameters">A document containing named parameter values.</param>
        /// <returns><see langword="true"/> if at least one matching document exists; otherwise, <see langword="false"/>.</returns>
        bool Exists(string predicate, BsonDocument parameters);

        /// <summary>
        /// Checks if any document matches a BSON expression with positional parameters without deserializing documents.
        /// </summary>
        /// <param name="predicate">The BSON expression string using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values.</param>
        /// <returns><see langword="true"/> if at least one matching document exists; otherwise, <see langword="false"/>.</returns>
        bool Exists(string predicate, params BsonValue[] args);

        /// <summary>
        /// Checks if any document matches a LINQ expression predicate without deserializing documents.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents.</param>
        /// <returns><see langword="true"/> if at least one matching document exists; otherwise, <see langword="false"/>.</returns>
        bool Exists(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Checks if any document matches a query without deserializing documents.
        /// </summary>
        /// <param name="query">The query object defining the filter.</param>
        /// <returns><see langword="true"/> if at least one matching document exists; otherwise, <see langword="false"/>.</returns>
        bool Exists(Query query);

        /// <summary>
        /// Returns the minimum value from the specified field across all documents in the collection.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the field to evaluate.</param>
        /// <returns>The minimum <see cref="BsonValue"/> found.</returns>
        BsonValue Min(BsonExpression keySelector);

        /// <summary>
        /// Returns the minimum <c>_id</c> value in the collection.
        /// </summary>
        /// <returns>The minimum <c>_id</c> as a <see cref="BsonValue"/>.</returns>
        BsonValue Min();

        /// <summary>
        /// Returns the minimum value from the specified field using a LINQ expression.
        /// </summary>
        /// <typeparam name="K">The type of the field to evaluate.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to evaluate.</param>
        /// <returns>The minimum value found.</returns>
        /// <example>
        /// <code>
        /// int minAge = col.Min(x => x.Age);
        /// </code>
        /// </example>
        K Min<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Returns the maximum value from the specified field across all documents in the collection.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the field to evaluate.</param>
        /// <returns>The maximum <see cref="BsonValue"/> found.</returns>
        BsonValue Max(BsonExpression keySelector);

        /// <summary>
        /// Returns the maximum <c>_id</c> value in the collection.
        /// </summary>
        /// <returns>The maximum <c>_id</c> as a <see cref="BsonValue"/>.</returns>
        BsonValue Max();

        /// <summary>
        /// Returns the maximum value from the specified field using a LINQ expression.
        /// </summary>
        /// <typeparam name="K">The type of the field to evaluate.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to evaluate.</param>
        /// <returns>The maximum value found.</returns>
        /// <example>
        /// <code>
        /// int maxAge = col.Max(x => x.Age);
        /// </code>
        /// </example>
        K Max<K>(Expression<Func<T, K>> keySelector);
    }
}