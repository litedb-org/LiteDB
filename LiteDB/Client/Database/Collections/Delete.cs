using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Delete a single document on collection based on _id index. Returns true if document was deleted
        /// </summary>
        public Task<bool> DeleteAsync(BsonValue id, CancellationToken cancellationToken = default)
        {
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));
            cancellationToken.ThrowIfCancellationRequested();

            var result = _engine.Delete(_collection, new[] { id }) == 1;

            return Task.FromResult(result);
        }

        /// <summary>
        /// Delete all documents inside collection. Returns how many documents was deleted. Run inside current transaction
        /// </summary>
        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = _engine.DeleteMany(_collection, null);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public Task<int> DeleteManyAsync(BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            cancellationToken.ThrowIfCancellationRequested();

            var result = _engine.DeleteMany(_collection, predicate);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public Task<int> DeleteManyAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default)
        {
            return this.DeleteManyAsync(BsonExpression.Create(predicate, parameters), cancellationToken);
        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public Task<int> DeleteManyAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args)
        {
            return this.DeleteManyAsync(BsonExpression.Create(predicate, args), cancellationToken);
        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public Task<int> DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return this.DeleteManyAsync(_mapper.GetExpression(predicate), cancellationToken);
        }
    }
}
