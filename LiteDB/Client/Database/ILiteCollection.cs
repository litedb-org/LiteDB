using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    public interface ILiteCollection<T>
    {
        /// <summary>
        /// Get collection name
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Get collection auto id type
        /// </summary>
        BsonAutoId AutoId { get; }

        /// <summary>
        /// Getting entity mapper from current collection. Returns null if collection are BsonDocument type
        /// </summary>
        EntityMapper EntityMapper { get; }

        /// <summary>
        /// Run an include action in each document returned by Find(), FindById(), FindOne() and All() methods to load DbRef documents
        /// Returns a new Collection with this action included
        /// </summary>
        ILiteCollection<T> Include<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Run an include action in each document returned by Find(), FindById(), FindOne() and All() methods to load DbRef documents
        /// Returns a new Collection with this action included
        /// </summary>
        ILiteCollection<T> Include(BsonExpression keySelector);

        /// <summary>
        /// Insert or Update a document in this collection.
        /// </summary>
        /// <param name="entity">The entity to insert or update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> UpsertAsync(T entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Insert or Update all documents
        /// </summary>
        /// <param name="entities">The entities to insert or update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> UpsertAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

        /// <summary>
        /// Insert or Update a document in this collection.
        /// </summary>
        /// <param name="id">The identifier of the entity.</param>
        /// <param name="entity">The entity to insert or update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> UpsertAsync(BsonValue id, T entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update a document in this collection. Returns false if not found document in collection
        /// </summary>
        /// <param name="entity">The entity to update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> UpdateAsync(T entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update a document in this collection. Returns false if not found document in collection
        /// </summary>
        /// <param name="id">The identifier of the entity.</param>
        /// <param name="entity">The entity to update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> UpdateAsync(BsonValue id, T entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update all documents
        /// </summary>
        /// <param name="entities">The entities to update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> UpdateAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update many documents based on transform expression. This expression must return a new document that will be replaced over current document (according with predicate).
        /// Eg: col.UpdateMany("{ Name: UPPER($.Name), Age }", "_id > 0")
        /// </summary>
        /// <param name="transform">The expression that produces the replacement document.</param>
        /// <param name="predicate">The filter expression that selects documents to update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> UpdateManyAsync(BsonExpression transform, BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update many document based on merge current document with extend expression. Use your class with initializers.
        /// Eg: col.UpdateMany(x => new Customer { Name = x.Name.ToUpper(), Salary: 100 }, x => x.Name == "John")
        /// </summary>
        /// <param name="extend">The expression that merges values into the existing document.</param>
        /// <param name="predicate">The filter expression that selects documents to update.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> UpdateManyAsync(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Insert a new entity to this collection. Document Id must be a new value in collection - Returns document Id
        /// </summary>
        /// <param name="entity">The entity to insert.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<BsonValue> InsertAsync(T entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Insert a new document to this collection using passed id value.
        /// </summary>
        /// <param name="id">The identifier to assign to the new entity.</param>
        /// <param name="entity">The entity to insert.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task InsertAsync(BsonValue id, T entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Insert an array of new documents to this collection. Document Id must be a new value in collection. Can be set buffer size to commit at each N documents
        /// </summary>
        /// <param name="entities">The entities to insert.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> InsertAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

        /// <summary>
        /// Implements bulk insert documents in a collection. Usefull when need lots of documents.
        /// </summary>
        /// <param name="entities">The entities to insert.</param>
        /// <param name="batchSize">The number of documents to batch together per transaction.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> InsertBulkAsync(IEnumerable<T> entities, int batchSize = 5000, CancellationToken cancellationToken = default);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="expression">Create a custom expression function to be indexed</param>
        /// <param name="unique">If is a unique index</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> EnsureIndexAsync(string name, BsonExpression expression, bool unique = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="expression">Document field/expression</param>
        /// <param name="unique">If is a unique index</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> EnsureIndexAsync(BsonExpression expression, bool unique = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> EnsureIndexAsync<K>(Expression<Func<T, K>> keySelector, bool unique = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> EnsureIndexAsync<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Drop index and release slot for another index
        /// </summary>
        /// <param name="name">The name of the index to drop.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> DropIndexAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>
        /// Return a new LiteQueryable to build more complex queries
        /// </summary>
        ILiteQueryable<T> Query();

        /// <summary>
        /// Find documents inside a collection using predicate expression.
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="skip">The number of documents to skip.</param>
        /// <param name="limit">The maximum number of documents to return.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        IAsyncEnumerable<T> FindAsync(BsonExpression predicate, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find documents inside a collection using query definition.
        /// </summary>
        /// <param name="query">The query definition to execute.</param>
        /// <param name="skip">The number of documents to skip.</param>
        /// <param name="limit">The maximum number of documents to return.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        IAsyncEnumerable<T> FindAsync(Query query, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find documents inside a collection using predicate expression.
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="skip">The number of documents to skip.</param>
        /// <param name="limit">The maximum number of documents to return.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        IAsyncEnumerable<T> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find a document using Document Id. Returns null if not found.
        /// </summary>
        /// <param name="id">The identifier of the document.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<T> FindByIdAsync(BsonValue id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<T> FindOneAsync(BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="parameters">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<T> FindOneAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="args">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<T> FindOneAsync(BsonExpression predicate, CancellationToken cancellationToken = default, params BsonValue[] args);

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<T> FindOneAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find the first document using defined query structure. Returns null if not found
        /// </summary>
        /// <param name="query">The query definition to execute.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<T> FindOneAsync(Query query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns all documents inside collection order by _id index.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        IAsyncEnumerable<T> FindAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete a single document on collection based on _id index. Returns true if document was deleted
        /// </summary>
        /// <param name="id">The identifier of the document to delete.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> DeleteAsync(BsonValue id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete all documents inside collection. Returns how many documents was deleted. Run inside current transaction
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> DeleteAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> DeleteManyAsync(BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="parameters">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> DeleteManyAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="args">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> DeleteManyAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args);

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get document count using property on collection.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> CountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> CountAsync(BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="parameters">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> CountAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="args">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> CountAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="query">The query definition to execute.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<int> CountAsync(Query query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get document count using property on collection.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<long> LongCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<long> LongCountAsync(BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="parameters">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<long> LongCountAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="args">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<long> LongCountAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<long> LongCountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        /// <param name="query">The query definition to execute.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<long> LongCountAsync(Query query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> ExistsAsync(BsonExpression predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="parameters">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> ExistsAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="args">Parameters used in the expression.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> ExistsAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args);

        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="predicate">The filter expression to apply.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        /// <param name="query">The query definition to execute.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<bool> ExistsAsync(Query query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        /// <param name="keySelector">The expression that selects the field to evaluate.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<BsonValue> MinAsync(BsonExpression keySelector, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the min value of _id index
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<BsonValue> MinAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        /// <param name="keySelector">The expression that selects the field to evaluate.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<K> MinAsync<K>(Expression<Func<T, K>> keySelector, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the max value from specified key value in collection
        /// </summary>
        /// <param name="keySelector">The expression that selects the field to evaluate.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<BsonValue> MaxAsync(BsonExpression keySelector, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the max _id index key value
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<BsonValue> MaxAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the last/max field using a linq expression
        /// </summary>
        /// <param name="keySelector">The expression that selects the field to evaluate.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        Task<K> MaxAsync<K>(Expression<Func<T, K>> keySelector, CancellationToken cancellationToken = default);
    }
}