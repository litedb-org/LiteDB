using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using LiteDB.Vector;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        // CollectionIndex.IndexType as reported by `$indexes`
        private const int SCALAR_INDEX_TYPE = 0;
        private const int VECTOR_INDEX_TYPE = 1;

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="expression">Create a custom expression function to be indexed</param>
        /// <param name="unique">If is a unique index</param>
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

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="expression">Document field/expression</param>
        /// <param name="unique">If is a unique index</param>
        public bool EnsureIndex(BsonExpression expression, bool unique = false)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            var name = GenerateIndexName(expression);
            var existing = this.FindIndexes(expression, SCALAR_INDEX_TYPE);

            // read-only lookup: an index that already gives the requested guarantee must not open a write transaction
            if (existing.Any(x => unique == false || x["unique"].AsBoolean)) return false;

            // the engine compares name and expression only, so it would report this non-unique index as ensured
            if (existing.Any(x => x["name"].AsString == name)) throw LiteException.IndexAlreadyExistNotUnique(name);

            return this.EnsureIndex(name, expression, unique);
        }

        internal bool EnsureVectorIndex(BsonExpression expression, VectorIndexOptions options)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var name = GenerateIndexName(expression);
            var existing = this.FindIndexes(expression, VECTOR_INDEX_TYPE).Select(x => x["name"].AsString).ToArray();

            // the engine still validates the vector options against the persisted index
            if (existing.Length > 0 && !existing.Contains(name)) name = existing[0];

            return this.EnsureVectorIndex(name, expression, options);
        }

        [Obsolete("Add `using LiteDB.Vector;` and call LiteCollectionVectorExtensions.EnsureIndex instead.")]
        public bool EnsureIndex(BsonExpression expression, VectorIndexOptions options)
        {
            return this.EnsureVectorIndex(expression, options);
        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
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

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
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

        private static string GenerateIndexName(BsonExpression expression)
        {
            return new string(expression.Source.Where(char.IsLetterOrDigit).ToArray());
        }

        /// <summary>
        /// `$indexes` rows of the indexes of this collection that already cover the expression. Generated names changed between
        /// versions and cultures (`$.Größe` was `Gre`), so auto-named overloads must match by expression, not by name.
        /// </summary>
        private BsonDocument[] FindIndexes(BsonExpression expression, int indexType)
        {
            return new LiteQueryable<BsonDocument>(_engine, _mapper, "$indexes", new Query())
                .ToDocuments()
                .Where(x => x["collection"].AsString.Equals(_collection, StringComparison.OrdinalIgnoreCase))
                .Where(x => x["expression"].AsString == expression.Source && x["indexType"].AsInt32 == indexType)
                .ToArray();
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

        /// <summary>
        /// Drop index and release slot for another index
        /// </summary>
        public bool DropIndex(string name)
        {
            return _engine.DropIndex(_collection, name);
        }
    }
}