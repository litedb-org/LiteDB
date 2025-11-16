using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using LiteDB.Vector;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <inheritdoc/>
        public bool EnsureIndex(string name, BsonExpression expression, bool unique = false)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            return _engine.EnsureIndex(_collection, name, expression, unique);
        }

        internal bool EnsureVectorIndex(string name, BsonExpression expression, VectorIndexOptions options)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            if (options == null) throw new ArgumentNullException(nameof(options));

            return _engine.EnsureVectorIndex(_collection, name, expression, options);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call LiteCollectionVectorExtensions.EnsureIndex instead.")]
        public bool EnsureIndex(string name, BsonExpression expression, VectorIndexOptions options)
        {
            return this.EnsureVectorIndex(name, expression, options);
        }

        /// <inheritdoc/>
        public bool EnsureIndex(BsonExpression expression, bool unique = false)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            var name = Regex.Replace(expression.Source, @"[^a-z0-9]", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            return this.EnsureIndex(name, expression, unique);
        }

        internal bool EnsureVectorIndex(BsonExpression expression, VectorIndexOptions options)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var name = Regex.Replace(expression.Source, @"[^a-z0-9]", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            return this.EnsureVectorIndex(name, expression, options);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call LiteCollectionVectorExtensions.EnsureIndex instead.")]
        public bool EnsureIndex(BsonExpression expression, VectorIndexOptions options)
        {
            return this.EnsureVectorIndex(expression, options);
        }

        /// <inheritdoc/>
        public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, bool unique = false)
        {
            var expression = this.GetIndexExpression(keySelector);

            return this.EnsureIndex(expression, unique);
        }

        internal bool EnsureVectorIndex<K>(Expression<Func<T, K>> keySelector, VectorIndexOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var expression = this.GetIndexExpression(keySelector, convertEnumerableToMultiKey: false);

            return this.EnsureVectorIndex(expression, options);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call LiteCollectionVectorExtensions.EnsureIndex instead.")]
        public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, VectorIndexOptions options)
        {
            return this.EnsureVectorIndex(keySelector, options);
        }

        /// <inheritdoc/>
        public bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            var expression = this.GetIndexExpression(keySelector);

            return this.EnsureIndex(name, expression, unique);
        }

        internal bool EnsureVectorIndex<K>(string name, Expression<Func<T, K>> keySelector, VectorIndexOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var expression = this.GetIndexExpression(keySelector, convertEnumerableToMultiKey: false);

            return this.EnsureVectorIndex(name, expression, options);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call LiteCollectionVectorExtensions.EnsureIndex instead.")]
        public bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, VectorIndexOptions options)
        {
            return this.EnsureVectorIndex(name, keySelector, options);
        }

        /// <summary>
        /// Get index expression based on LINQ expression. Convert IEnumerable in MultiKey indexes
        /// </summary>
        private BsonExpression GetIndexExpression<K>(Expression<Func<T, K>> keySelector, bool convertEnumerableToMultiKey = true)
        {
            var expression = _mapper.GetIndexExpression(keySelector);

            if (convertEnumerableToMultiKey && typeof(K).IsEnumerable() && expression.IsScalar == true)
            {
                if (expression.Type == BsonExpressionType.Path)
                {
                    // convert LINQ expression that returns an IEnumerable but expression returns a single value
                    // `x => x.Phones` --> `$.Phones[*]`
                    // works only if exression is a simple path
                    expression = expression.Source + "[*]";
                }
                else
                {
                    throw new LiteException(0, $"Expression `{expression.Source}` must return a enumerable expression");
                }
            }

            return expression;
        }

        /// <inheritdoc/>
        public bool DropIndex(string name)
        {
            return _engine.DropIndex(_collection, name);
        }
    }
}