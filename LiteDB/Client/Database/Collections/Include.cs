using System;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <inheritdoc/>
        public ILiteCollection<T> Include<K>(Expression<Func<T, K>> keySelector)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var path = _mapper.GetExpression(keySelector);

            return this.Include(path);
        }

        /// <inheritdoc/>
        public ILiteCollection<T> Include(BsonExpression keySelector)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            // cloning this collection and adding this include
            var newcol = new LiteCollection<T>(_collection, _autoId, _engine, _mapper);

            newcol._includes.AddRange(_includes);
            newcol._includes.Add(keySelector);

            return newcol;
        }
    }
}