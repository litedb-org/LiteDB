using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    public interface ILiteRepository : IDisposable
    {
        /// <summary>
        /// Get database instance
        /// </summary>
        ILiteDatabase Database { get; }

        /// <summary>
        /// Insert a new document into collection. Document Id must be a new value in collection - Returns document Id
        /// </summary>
        BsonValue Insert<T>(T entity, string collectionName = null);

        /// <summary>
        /// Insert an array of new documents into collection. Document Id must be a new value in collection. Can be set buffer size to commit at each N documents
        /// </summary>
        int Insert<T>(IEnumerable<T> entities, string collectionName = null);

        /// <summary>
        /// Update a document into collection. Returns false if not found document in collection
        /// </summary>
        bool Update<T>(T entity, string collectionName = null);

        /// <summary>
        /// Update all documents
        /// </summary>
        int Update<T>(IEnumerable<T> entities, string collectionName = null);

        /// <summary>
        /// Insert or Update a document based on _id key. Returns true if insert entity or false if update entity
        /// </summary>
        bool Upsert<T>(T entity, string collectionName = null);

        /// <summary>
        /// Insert or Update all documents based on _id key. Returns entity count that was inserted
        /// </summary>
        int Upsert<T>(IEnumerable<T> entities, string collectionName = null);

        /// <summary>
        /// Delete entity based on _id key
        /// </summary>
        bool Delete<T>(BsonValue id, string collectionName = null);

        /// <summary>
        /// Delete entity based on Query
        /// </summary>
        int DeleteMany<T>(BsonExpression predicate, string collectionName = null);

        /// <summary>
        /// Delete entity based on predicate filter expression
        /// </summary>
        int DeleteMany<T>(Expression<Func<T, bool>> predicate, string collectionName = null);

        /// <summary>
        /// Returns new instance of LiteQueryable that provides all method to query any entity inside collection. Use fluent API to apply filter/includes an than run any execute command, like ToList() or First()
        /// </summary>
        ILiteQueryable<T> Query<T>(string collectionName = null);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="expression">Create a custom expression function to be indexed</param>
        /// <param name="unique">If is a unique index</param>
        /// <param name="collectionName">Collection Name</param>
        bool EnsureIndex<T>(string name, BsonExpression expression, bool unique = false, string collectionName = null);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="expression">Create a custom expression function to be indexed</param>
        /// <param name="unique">If is a unique index</param>
        /// <param name="collectionName">Collection Name</param>
        bool EnsureIndex<T>(BsonExpression expression, bool unique = false, string collectionName = null);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        /// <param name="collectionName">Collection Name</param>
        bool EnsureIndex<T, K>(Expression<Func<T, K>> keySelector, bool unique = false, string collectionName = null);

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        /// <param name="collectionName">Collection Name</param>
        bool EnsureIndex<T, K>(string name, Expression<Func<T, K>> keySelector, bool unique = false, string collectionName = null);

        /// <summary>
        /// Search for a single instance of T by Id. Shortcut from Query.SingleById
        /// </summary>
        T SingleById<T>(BsonValue id, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).ToList();
        /// </summary>
        List<T> Fetch<T>(BsonExpression predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).ToList();
        /// </summary>
        List<T> Fetch<T>(Expression<Func<T, bool>> predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).First();
        /// </summary>
        T First<T>(BsonExpression predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).First();
        /// </summary>
        T First<T>(Expression<Func<T, bool>> predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).FirstOrDefault();
        /// </summary>
        T FirstOrDefault<T>(BsonExpression predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).FirstOrDefault();
        /// </summary>
        T FirstOrDefault<T>(Expression<Func<T, bool>> predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).Single();
        /// </summary>
        T Single<T>(BsonExpression predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).Single();
        /// </summary>
        T Single<T>(Expression<Func<T, bool>> predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).SingleOrDefault();
        /// </summary>
        T SingleOrDefault<T>(BsonExpression predicate, string collectionName = null);

        /// <summary>
        /// Execute Query[T].Where(predicate).SingleOrDefault();
        /// </summary>
        T SingleOrDefault<T>(Expression<Func<T, bool>> predicate, string collectionName = null);
    }
}