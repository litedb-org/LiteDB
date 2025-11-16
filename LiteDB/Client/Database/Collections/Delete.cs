using System;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <inheritdoc/>
        public bool Delete(BsonValue id)
        {
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            return _engine.Delete(_collection, new [] { id }) == 1;
        }

        /// <inheritdoc/>
        public int DeleteAll()
        {
            return _engine.DeleteMany(_collection, null);
        }

        /// <inheritdoc/>
        public int DeleteMany(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return _engine.DeleteMany(_collection, predicate);
        }

        /// <inheritdoc/>
        public int DeleteMany(string predicate, BsonDocument parameters) => this.DeleteMany(BsonExpression.Create(predicate, parameters));

        /// <inheritdoc/>
        public int DeleteMany(string predicate, params BsonValue[] args) => this.DeleteMany(BsonExpression.Create(predicate, args));

        /// <inheritdoc/>
        public int DeleteMany(Expression<Func<T, bool>> predicate) => this.DeleteMany(_mapper.GetExpression(predicate));
    }
}