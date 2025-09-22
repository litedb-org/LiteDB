using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="expression">Create a custom expression function to be indexed</param>
        /// <param name="unique">If is a unique index</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        public Task<bool> EnsureIndexAsync(string name, BsonExpression expression, bool unique = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            cancellationToken.ThrowIfCancellationRequested();

            var result = _engine.EnsureIndex(_collection, name, expression, unique);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="expression">Document field/expression</param>
        /// <param name="unique">If is a unique index</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        public Task<bool> EnsureIndexAsync(BsonExpression expression, bool unique = false, CancellationToken cancellationToken = default)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            var name = Regex.Replace(expression.Source, @"[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Compiled);

            return this.EnsureIndexAsync(name, expression, unique, cancellationToken);
        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        public Task<bool> EnsureIndexAsync<K>(Expression<Func<T, K>> keySelector, bool unique = false, CancellationToken cancellationToken = default)
        {
            var expression = this.GetIndexExpression(keySelector);

            return this.EnsureIndexAsync(expression, unique, cancellationToken);
        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        public Task<bool> EnsureIndexAsync<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false, CancellationToken cancellationToken = default)
        {
            var expression = this.GetIndexExpression(keySelector);

            return this.EnsureIndexAsync(name, expression, unique, cancellationToken);
        }

        /// <summary>
        /// Get index expression based on LINQ expression. Convert IEnumerable in MultiKey indexes
        /// </summary>
        private BsonExpression GetIndexExpression<K>(Expression<Func<T, K>> keySelector)
        {
            var expression = _mapper.GetIndexExpression(keySelector);

            if (typeof(K).IsEnumerable() && expression.IsScalar == true)
            {
                if (expression.Type == BsonExpressionType.Path)
                {
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
        /// <param name="name">The index name to drop.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        public Task<bool> DropIndexAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = _engine.DropIndex(_collection, name);

            return Task.FromResult(result);
        }
    }
}
