using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

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

        /// <summary>
        /// Filters documents where the given vector field is within cosine distance from the target vector.
        /// </summary>
        ILiteQueryable<T> WhereNear(string vectorField, float[] target, double maxDistance);
        
        /// <summary>
        /// Immediately returns documents nearest to the target vector based on cosine distance.
        /// </summary>
        IEnumerable<T> FindNearest(string vectorField, float[] target, double maxDistance);
        ILiteQueryable<T> WhereNear<K>(Expression<Func<T, K>> field, float[] target, double maxDistance);
        ILiteQueryable<T> WhereNear(BsonExpression fieldExpr, float[] target, double maxDistance);
        ILiteQueryableResult<T> TopKNear<K>(Expression<Func<T, K>> field, float[] target, int k);
        ILiteQueryableResult<T> TopKNear(string field, float[] target, int k);
        ILiteQueryableResult<T> TopKNear(BsonExpression fieldExpr, float[] target, int k);
    }

    public interface ILiteQueryableResult<T>
    {
        ILiteQueryableResult<T> Limit(int limit);
        ILiteQueryableResult<T> Skip(int offset);
        ILiteQueryableResult<T> Offset(int offset);
        ILiteQueryableResult<T> ForUpdate();

        BsonDocument GetPlan();
        IBsonDataReader ExecuteReader();
        IEnumerable<BsonDocument> ToDocuments();
        IEnumerable<T> ToEnumerable();
        List<T> ToList();
        T[] ToArray();

        int Into(string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId);

        T First();
        T FirstOrDefault();
        T Single();
        T SingleOrDefault();

        int Count();
        long LongCount();
        bool Exists();
    }
}