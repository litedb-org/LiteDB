using LiteDB.Engine;

using System;
using System.Collections.Generic;
using System.Linq;

using static LiteDB.Constants;

namespace LiteDB
{
    public partial class Query
    {
        /// <summary>
        /// Constant indicating ascending sort order (1).
        /// </summary>
        public const int Ascending = 1;

        /// <summary>
        /// Constant indicating descending sort order (-1).
        /// </summary>
        public const int Descending = -1;

        /// <summary>
        /// Creates a query that returns all documents in the collection without ordering.
        /// </summary>
        /// <returns>A <see cref="Query"/> instance that selects all documents.</returns>
        public static Query All()
        {
            return new Query();
        }

        /// <summary>
        /// Creates a query that returns all documents ordered by the <c>_id</c> field.
        /// </summary>
        /// <param name="order">The sort order: <see cref="Ascending"/> (1) or <see cref="Descending"/> (-1). Default is <see cref="Ascending"/>.</param>
        /// <returns>A <see cref="Query"/> instance that selects all documents ordered by <c>_id</c>.</returns>
        public static Query All(int order = Ascending)
        {
            var query = new Query();
            query.OrderBy.Add(new QueryOrder(BsonExpression.Create("_id"), order));
            return query;
        }

        /// <summary>
        /// Creates a query that returns all documents ordered by the specified field.
        /// </summary>
        /// <param name="field">The field name to order by.</param>
        /// <param name="order">The sort order: <see cref="Ascending"/> (1) or <see cref="Descending"/> (-1). Default is <see cref="Ascending"/>.</param>
        /// <returns>A <see cref="Query"/> instance that selects all documents ordered by the specified field.</returns>
        public static Query All(string field, int order = Ascending)
        {
            var query = new Query();
            query.OrderBy.Add(new QueryOrder(BsonExpression.Create(field), order));
            return query;
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value equals the specified value.
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="value">The value to match.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the equality comparison (=).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression EQ(string field, BsonValue value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} = {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is less than the specified value.
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the less-than comparison (&lt;).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression LT(string field, BsonValue value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} < {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is less than or equal to the specified value.
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the less-than-or-equal comparison (&lt;=).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression LTE(string field, BsonValue value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} <= {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is greater than the specified value.
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the greater-than comparison (&gt;).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression GT(string field, BsonValue value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} > {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is greater than or equal to the specified value.
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the greater-than-or-equal comparison (&gt;=).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression GTE(string field, BsonValue value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} >= {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is between the start and end values (inclusive).
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="start">The start value of the range (inclusive).</param>
        /// <param name="end">The end value of the range (inclusive).</param>
        /// <returns>A <see cref="BsonExpression"/> representing the BETWEEN comparison.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression Between(string field, BsonValue start, BsonValue end)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} BETWEEN {start ?? BsonValue.Null} AND {end ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the string field value starts with the specified prefix.
        /// </summary>
        /// <param name="field">The field name containing the string to check.</param>
        /// <param name="value">The prefix string to match.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the LIKE comparison with wildcard suffix.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> or <paramref name="value"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression StartsWith(string field, string value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
            if (value.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(value));

            return BsonExpression.Create($"{field} LIKE {(new BsonValue(value + "%"))}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the string field value contains the specified substring.
        /// </summary>
        /// <param name="field">The field name containing the string to search.</param>
        /// <param name="value">The substring to find.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the LIKE comparison with wildcard prefix and suffix.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> or <paramref name="value"/> is <see langword="null"/> or empty.</exception>
        public static BsonExpression Contains(string field, string value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
            if (value.IsNullOrEmpty()) throw new ArgumentNullException(nameof(value));

            return BsonExpression.Create($"{field} LIKE {(new BsonValue("%" + value + "%"))}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is not equal to the specified value.
        /// </summary>
        /// <param name="field">The field name to compare.</param>
        /// <param name="value">The value to exclude.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the not-equal comparison (!=).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace.</exception>
        public static BsonExpression Not(string field, BsonValue value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

            return BsonExpression.Create($"{field} != {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is in the specified array of values.
        /// </summary>
        /// <param name="field">The field name to check.</param>
        /// <param name="value">The array of values to match against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the IN comparison.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace, or when <paramref name="value"/> is <see langword="null"/>.</exception>
        public static BsonExpression In(string field, BsonArray value)
        {
            if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
            if (value == null) throw new ArgumentNullException(nameof(value));

            return BsonExpression.Create($"{field} IN {value}");
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is in the specified array of values.
        /// </summary>
        /// <param name="field">The field name to check.</param>
        /// <param name="values">The array of values to match against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the IN comparison.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is <see langword="null"/> or whitespace, or when <paramref name="values"/> is <see langword="null"/>.</exception>
        public static BsonExpression In(string field, params BsonValue[] values)
        {
            return In(field, new BsonArray(values));
        }

        /// <summary>
        /// Creates an expression that matches documents where the field value is in the specified collection of values.
        /// </summary>
        /// <param name="field">The field name to check.</param>
        /// <param name="values">The collection of values to match against.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the IN comparison.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="field"/> is null or whitespace.</exception>
        public static BsonExpression In(string field, IEnumerable<BsonValue> values)
        {
            return In(field, new BsonArray(values));
        }

        /// <summary>
        /// Gets a <see cref="QueryAny"/> instance that provides methods for working with array or enumerable field values.
        /// </summary>
        /// <returns>A <see cref="QueryAny"/> instance for array operations.</returns>
        public static QueryAny Any() => new QueryAny();

        /// <summary>
        /// Combines two expressions with logical AND, matching documents that satisfy both conditions.
        /// </summary>
        /// <param name="left">The left expression.</param>
        /// <param name="right">The right expression.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the AND combination.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// If both expressions use indexes, the left expression's index is preferred and the right side may perform a full scan.
        /// </remarks>
        public static BsonExpression And(BsonExpression left, BsonExpression right)
        {
            if (left == null) throw new ArgumentNullException(nameof(left));
            if (right == null) throw new ArgumentNullException(nameof(right));

            return $"({left.Source} AND {right.Source})";
        }

        /// <summary>
        /// Combines multiple expressions with logical AND, matching documents that satisfy all conditions.
        /// </summary>
        /// <param name="queries">The array of expressions to combine. At least two expressions are required.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the AND combination of all expressions.</returns>
        /// <exception cref="ArgumentException">Thrown when fewer than two expressions are provided.</exception>
        public static BsonExpression And(params BsonExpression[] queries)
        {
            if (queries == null || queries.Length < 2) throw new ArgumentException("At least two Query should be passed");

            var left = queries[0];

            for (int i = 1; i < queries.Length; i++)
            {
                left = And(left, queries[i]);
            }

            return left;
        }

        /// <summary>
        /// Combines two expressions with logical OR, matching documents that satisfy either condition (union).
        /// </summary>
        /// <param name="left">The left expression.</param>
        /// <param name="right">The right expression.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the OR combination.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.</exception>
        public static BsonExpression Or(BsonExpression left, BsonExpression right)
        {
            if (left == null) throw new ArgumentNullException(nameof(left));
            if (right == null) throw new ArgumentNullException(nameof(right));

            return $"({left.Source} OR {right.Source})";
        }

        /// <summary>
        /// Combines multiple expressions with logical OR, matching documents that satisfy any condition (union).
        /// </summary>
        /// <param name="queries">The array of expressions to combine. At least two expressions are required.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the OR combination of all expressions.</returns>
        /// <exception cref="ArgumentException">Thrown when fewer than two expressions are provided.</exception>
        public static BsonExpression Or(params BsonExpression[] queries)
        {
            if (queries == null || queries.Length < 2) throw new ArgumentException("At least two Query should be passed");

            var left = queries[0];

            for (int i = 1; i < queries.Length; i++)
            {
                left = Or(left, queries[i]);
            }

            return left;
        }
    }
}