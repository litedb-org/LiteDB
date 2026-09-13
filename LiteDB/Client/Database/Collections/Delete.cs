using System;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Delete a single document on collection based on _id index. Returns true if document was deleted
        /// </summary>
        public bool Delete(BsonValue id)
        {
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            return _engine.Delete(_collection, new [] { id }) == 1;
        }

        /// <summary>
        /// Delete all documents inside collection. Returns how many documents was deleted. Run inside current transaction
        /// </summary>
        public int DeleteAll()
        {
            return _engine.DeleteMany(_collection, null);
        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return _engine.DeleteMany(_collection, predicate);
        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(string predicate, BsonDocument parameters) => this.DeleteMany(BsonExpression.Create(predicate, parameters));

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(string predicate, params BsonValue[] args) => this.DeleteMany(BsonExpression.Create(predicate, args));

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(Expression<Func<T, bool>> predicate) => this.DeleteMany(_mapper.GetExpression(predicate));
    }
}