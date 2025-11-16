using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    /// <summary>
    /// Provides a fluent query interface for building and executing queries against a LiteDB collection.
    /// </summary>
    /// <typeparam name="T">The entity type for the query results.</typeparam>
    /// <remarks>
    /// Methods can be chained to build complex queries.
    /// <para>Call a result method (e.g., <see cref="ILiteQueryableResult{T}.ToList"/>) to execute the query.</para>
    /// </remarks>
    public interface ILiteQueryable<T> : ILiteQueryableResult<T>
    {
        /// <summary>
        /// Includes related documents via DbRef using a BSON expression path.
        /// </summary>
        /// <param name="path">The BSON expression path to the DbRef field.</param>
        /// <returns>A new queryable with the include applied.</returns>
        ILiteQueryable<T> Include(BsonExpression path);

        /// <summary>
        /// Includes multiple related documents via DbRef using a list of BSON expression paths.
        /// </summary>
        /// <param name="paths">The list of BSON expression paths to DbRef fields.</param>
        /// <returns>A new queryable with the includes applied.</returns>
        ILiteQueryable<T> Include(List<BsonExpression> paths);

        /// <summary>
        /// Includes related documents via DbRef using a LINQ expression path.
        /// </summary>
        /// <typeparam name="K">The type of the related document or collection.</typeparam>
        /// <param name="path">A LINQ expression selecting the DbRef field.</param>
        /// <returns>A new queryable with the include applied.</returns>
        ILiteQueryable<T> Include<K>(Expression<Func<T, K>> path);

        /// <summary>
        /// Filters documents based on a BSON expression predicate.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter documents.</param>
        /// <returns>A new queryable with the filter applied.</returns>
        ILiteQueryable<T> Where(BsonExpression predicate);

        /// <summary>
        /// Filters documents based on a BSON expression with named parameters.
        /// </summary>
        /// <param name="predicate">The BSON expression string to filter documents.</param>
        /// <param name="parameters">A document containing named parameter values.</param>
        /// <returns>A new queryable with the filter applied.</returns>
        ILiteQueryable<T> Where(string predicate, BsonDocument parameters);

        /// <summary>
        /// Filters documents based on a BSON expression with positional parameters.
        /// </summary>
        /// <param name="predicate">The BSON expression string using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values.</param>
        /// <returns>A new queryable with the filter applied.</returns>
        ILiteQueryable<T> Where(string predicate, params BsonValue[] args);

        /// <summary>
        /// Filters documents based on a LINQ expression predicate.
        /// </summary>
        /// <param name="predicate">A LINQ expression to filter documents.</param>
        /// <returns>A new queryable with the filter applied.</returns>
        ILiteQueryable<T> Where(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Orders documents by a BSON expression in ascending (1) or descending (-1) order.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the field to sort by.</param>
        /// <param name="order">The sort order: 1 for ascending, -1 for descending. Default is 1.</param>
        /// <returns>A new queryable with the ordering applied.</returns>
        ILiteQueryable<T> OrderBy(BsonExpression keySelector, int order = 1);

        /// <summary>
        /// Orders documents by a LINQ expression in ascending (1) or descending (-1) order.
        /// </summary>
        /// <typeparam name="K">The type of the field to sort by.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to sort by.</param>
        /// <param name="order">The sort order: 1 for ascending, -1 for descending. Default is 1.</param>
        /// <returns>A new queryable with the ordering applied.</returns>
        ILiteQueryable<T> OrderBy<K>(Expression<Func<T, K>> keySelector, int order = 1);

        /// <summary>
        /// Orders documents by a BSON expression in descending order.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the field to sort by.</param>
        /// <returns>A new queryable with the ordering applied.</returns>
        ILiteQueryable<T> OrderByDescending(BsonExpression keySelector);

        /// <summary>
        /// Orders documents by a LINQ expression in descending order.
        /// </summary>
        /// <typeparam name="K">The type of the field to sort by.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to sort by.</param>
        /// <returns>A new queryable with the ordering applied.</returns>
        ILiteQueryable<T> OrderByDescending<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Applies a secondary ascending sort by a BSON expression.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the field to sort by.</param>
        /// <returns>A new queryable with the secondary ordering applied.</returns>
        ILiteQueryable<T> ThenBy(BsonExpression keySelector);

        /// <summary>
        /// Applies a secondary ascending sort by a LINQ expression.
        /// </summary>
        /// <typeparam name="K">The type of the field to sort by.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to sort by.</param>
        /// <returns>A new queryable with the secondary ordering applied.</returns>
        ILiteQueryable<T> ThenBy<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Applies a secondary descending sort by a BSON expression.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the field to sort by.</param>
        /// <returns>A new queryable with the secondary ordering applied.</returns>
        ILiteQueryable<T> ThenByDescending(BsonExpression keySelector);

        /// <summary>
        /// Applies a secondary descending sort by a LINQ expression.
        /// </summary>
        /// <typeparam name="K">The type of the field to sort by.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the field to sort by.</param>
        /// <returns>A new queryable with the secondary ordering applied.</returns>
        ILiteQueryable<T> ThenByDescending<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Groups documents by a LINQ expression key.
        /// </summary>
        /// <typeparam name="K">The type of the grouping key.</typeparam>
        /// <param name="keySelector">A LINQ expression selecting the grouping key.</param>
        /// <returns>A queryable of grouped results.</returns>
        ILiteQueryable<IGrouping<K, T>> GroupBy<K>(Expression<Func<T, K>> keySelector);

        /// <summary>
        /// Groups documents by a BSON expression key.
        /// </summary>
        /// <param name="keySelector">The BSON expression selecting the grouping key.</param>
        /// <returns>A new queryable with grouping applied.</returns>
        ILiteQueryable<T> GroupBy(BsonExpression keySelector);

        /// <summary>
        /// Filters grouped results based on a BSON expression predicate.
        /// </summary>
        /// <param name="predicate">The BSON expression to filter grouped results.</param>
        /// <returns>A new queryable with the having filter applied.</returns>
        ILiteQueryable<T> Having(BsonExpression predicate);

        /// <summary>
        /// Projects documents to <see cref="BsonDocument"/> using a BSON expression.
        /// </summary>
        /// <param name="selector">The BSON expression defining the projection.</param>
        /// <returns>A queryable of <see cref="BsonDocument"/> results.</returns>
        ILiteQueryable<BsonDocument> Select(BsonExpression selector);

        /// <summary>
        /// Projects documents to a new type using a LINQ expression.
        /// </summary>
        /// <typeparam name="K">The type of the projected result.</typeparam>
        /// <param name="selector">A LINQ expression defining the projection.</param>
        /// <returns>A queryable of projected results.</returns>
        ILiteQueryable<K> Select<K>(Expression<Func<T, K>> selector);
    }

    /// <summary>
    /// Provides result execution methods for a LiteDB query.
    /// </summary>
    /// <typeparam name="T">The entity type for the query results.</typeparam>
    public interface ILiteQueryableResult<T>
    {
        /// <summary>
        /// Limits the number of documents returned by the query.
        /// </summary>
        /// <param name="limit">The maximum number of documents to return.</param>
        /// <returns>A new queryable with the limit applied.</returns>
        ILiteQueryableResult<T> Limit(int limit);

        /// <summary>
        /// Skips the specified number of documents in the query results.
        /// </summary>
        /// <param name="offset">The number of documents to skip.</param>
        /// <returns>A new queryable with the skip applied.</returns>
        ILiteQueryableResult<T> Skip(int offset);

        /// <summary>
        /// Skips the specified number of documents in the query results.
        /// <para>Alias for <see cref="Skip"/>.</para>
        /// </summary>
        /// <param name="offset">The number of documents to skip.</param>
        /// <returns>A new queryable with the offset applied.</returns>
        ILiteQueryableResult<T> Offset(int offset);

        /// <summary>
        /// Marks the query for update, acquiring a write lock on matching documents.
        /// </summary>
        /// <returns>A new queryable configured for update operations.</returns>
        /// <remarks>
        /// Use this when you need to read documents and immediately update them within a transaction to prevent race conditions.
        /// </remarks>
        ILiteQueryableResult<T> ForUpdate();

        /// <summary>
        /// Gets the query execution plan as a <see cref="BsonDocument"/>.
        /// </summary>
        /// <returns>A document describing how the query will be executed, including index usage.</returns>
        BsonDocument GetPlan();

        /// <summary>
        /// Executes the query and returns a data reader for streaming results.
        /// </summary>
        /// <returns>An <see cref="IBsonDataReader"/> for reading query results.</returns>
        IBsonDataReader ExecuteReader();

        /// <summary>
        /// Executes the query and returns results as <see cref="BsonDocument"/> instances.
        /// </summary>
        /// <returns>An enumerable collection of <see cref="BsonDocument"/> results.</returns>
        IEnumerable<BsonDocument> ToDocuments();

        /// <summary>
        /// Executes the query and returns results as an enumerable collection.
        /// </summary>
        /// <returns>An enumerable collection of typed results.</returns>
        IEnumerable<T> ToEnumerable();

        /// <summary>
        /// Executes the query and returns results as a list.
        /// </summary>
        /// <returns>A list of typed results.</returns>
        List<T> ToList();

        /// <summary>
        /// Executes the query and returns results as an array.
        /// </summary>
        /// <returns>An array of typed results.</returns>
        T[] ToArray();

        /// <summary>
        /// Executes the query and inserts results into a new collection.
        /// </summary>
        /// <param name="newCollection">The name of the new collection to create.</param>
        /// <param name="autoId">The auto-ID strategy for the new collection. Default is <see cref="BsonAutoId.ObjectId"/>.</param>
        /// <returns>The number of documents inserted into the new collection.</returns>
        int Into(string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId);

        /// <summary>
        /// Returns the first document from the query results.
        /// </summary>
        /// <returns>The first document.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the query returns no results.</exception>
        T First();

        /// <summary>
        /// Returns the first document from the query results, or <see langword="null"/> if no results.
        /// </summary>
        /// <returns>The first document, or the default value for <typeparamref name="T"/>.</returns>
        T FirstOrDefault();

        /// <summary>
        /// Returns the only document from the query results.
        /// </summary>
        /// <returns>The single document.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the query returns zero or more than one document.</exception>
        T Single();

        /// <summary>
        /// Returns the only document from the query results, or <see langword="null"/> if no results.
        /// </summary>
        /// <returns>The single document, or the default value for <typeparamref name="T"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the query returns more than one document.</exception>
        T SingleOrDefault();

        /// <summary>
        /// Counts the number of documents that match the query.
        /// </summary>
        /// <returns>The count of matching documents.</returns>
        int Count();

        /// <summary>
        /// Counts the number of documents that match the query as a <see cref="long"/>.
        /// </summary>
        /// <returns>The count of matching documents.</returns>
        long LongCount();

        /// <summary>
        /// Determines whether any documents match the query.
        /// </summary>
        /// <returns><see langword="true"/> if at least one document matches; otherwise, <see langword="false"/>.</returns>
        bool Exists();
    }
}