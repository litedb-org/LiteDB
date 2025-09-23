using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    public interface ILiteQueryable<T> : ILiteQueryableResult<T>
    {
        ILiteQueryable<T> Include(BsonExpression path);
        ILiteQueryable<T> Include(List<BsonExpression> paths);
        ILiteQueryable<T> Include<K>(Expression<Func<T, K>> path);

        ILiteQueryable<T> Where(BsonExpression predicate);
        ILiteQueryable<T> Where(string predicate, BsonDocument parameters);
        ILiteQueryable<T> Where(string predicate, params BsonValue[] args);
        ILiteQueryable<T> Where(Expression<Func<T, bool>> predicate);

        ILiteQueryable<T> OrderBy(BsonExpression keySelector, int order = 1);
        ILiteQueryable<T> OrderBy<K>(Expression<Func<T, K>> keySelector, int order = 1);
        ILiteQueryable<T> OrderByDescending(BsonExpression keySelector);
        ILiteQueryable<T> OrderByDescending<K>(Expression<Func<T, K>> keySelector);
        ILiteQueryable<T> ThenBy(BsonExpression keySelector);
        ILiteQueryable<T> ThenBy<K>(Expression<Func<T, K>> keySelector);
        ILiteQueryable<T> ThenByDescending(BsonExpression keySelector);
        ILiteQueryable<T> ThenByDescending<K>(Expression<Func<T, K>> keySelector);

        ILiteQueryable<T> GroupBy(BsonExpression keySelector);
        ILiteQueryable<T> Having(BsonExpression predicate);

        ILiteQueryableResult<BsonDocument> Select(BsonExpression selector);
        ILiteQueryableResult<K> Select<K>(Expression<Func<T, K>> selector);
    }

    public interface ILiteQueryableResult<T>
    {
        ILiteQueryableResult<T> Limit(int limit);
        ILiteQueryableResult<T> Skip(int offset);
        ILiteQueryableResult<T> Offset(int offset);
        ILiteQueryableResult<T> ForUpdate();

        Task<BsonDocument> GetPlanAsync(CancellationToken cancellationToken = default);
        Task<IBsonDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default);
        IAsyncEnumerable<BsonDocument> ToDocumentsAsync(CancellationToken cancellationToken = default);
        IAsyncEnumerable<T> ToAsyncEnumerable(CancellationToken cancellationToken = default);
        Task<List<T>> ToListAsync(CancellationToken cancellationToken = default);
        Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default);

        Task<int> IntoAsync(string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId, CancellationToken cancellationToken = default);

        Task<T> FirstAsync(CancellationToken cancellationToken = default);
        Task<T> FirstOrDefaultAsync(CancellationToken cancellationToken = default);
        Task<T> SingleAsync(CancellationToken cancellationToken = default);
        Task<T> SingleOrDefaultAsync(CancellationToken cancellationToken = default);

        Task<int> CountAsync(CancellationToken cancellationToken = default);
        Task<long> LongCountAsync(CancellationToken cancellationToken = default);
        Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    }
}