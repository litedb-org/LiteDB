using System;
using System.Collections.Generic;

namespace LiteDB
{
    public partial class Query
    {
        /// <summary>
        /// Opt-in helpers with reusable parameterized sources. Preserve both Source
        /// and Parameters when forwarding expressions; Source alone is not a value snapshot.
        /// </summary>
        public static class Parameterized
        {
            /// <summary>
            /// Returns all documents that value are equals to value (=)
            /// </summary>
            public static BsonExpression EQ(string field, BsonValue value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, "= {0}", value);
            }

            /// <summary>
            /// Returns all documents that value are less than value (&lt;)
            /// </summary>
            public static BsonExpression LT(string field, BsonValue value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, "< {0}", value);
            }

            /// <summary>
            /// Returns all documents that value are less than or equals value (&lt;=)
            /// </summary>
            public static BsonExpression LTE(string field, BsonValue value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, "<= {0}", value);
            }

            /// <summary>
            /// Returns all document that value are greater than value (&gt;)
            /// </summary>
            public static BsonExpression GT(string field, BsonValue value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, "> {0}", value);
            }

            /// <summary>
            /// Returns all documents that value are greater than or equals value (&gt;=)
            /// </summary>
            public static BsonExpression GTE(string field, BsonValue value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, ">= {0}", value);
            }

            /// <summary>
            /// Returns all document that values are between "start" and "end" values (BETWEEN)
            /// </summary>
            public static BsonExpression Between(string field, BsonValue start, BsonValue end)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, "BETWEEN {0} AND {1}", start, end);
            }

            /// <summary>
            /// Returns all documents that starts with value (LIKE)
            /// </summary>
            public static BsonExpression StartsWith(string field, string value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
                if (value.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(value));

                return Query.CreateValueQuery(field, "LIKE {0}", new BsonValue(value + "%"));
            }

            /// <summary>
            /// Returns all documents that ends with value (LIKE)
            /// </summary>
            public static BsonExpression EndsWith(string field, string value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
                if (value.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(value));

                return Query.CreateValueQuery(field, "LIKE {0}", new BsonValue("%" + value));
            }


            /// <summary>
            /// Returns all documents that contains value (CONTAINS) - string Contains
            /// </summary>
            public static BsonExpression Contains(string field, string value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
                if (value.IsNullOrEmpty()) throw new ArgumentNullException(nameof(value));

                return Query.CreateValueQuery(field, "LIKE {0}", new BsonValue("%" + value + "%"));
            }

            /// <summary>
            /// Returns all documents that are not equals to value (not equals)
            /// </summary>
            public static BsonExpression Not(string field, BsonValue value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));

                return Query.CreateValueQuery(field, "!= {0}", value);
            }

            /// <summary>
            /// Returns all documents that has value in values list (IN)
            /// </summary>
            public static BsonExpression In(string field, BsonArray value)
            {
                if (field.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(field));
                if (value == null) throw new ArgumentNullException(nameof(value));

                return Query.CreateValueQuery(field, "IN {0}", value);
            }

            /// <summary>
            /// Returns all documents that has value in values list (IN)
            /// </summary>
            public static BsonExpression In(string field, params BsonValue[] values)
            {
                return In(field, new BsonArray(values));
            }

            /// <summary>
            /// Returns all documents that has value in values list (IN)
            /// </summary>
            public static BsonExpression In(string field, IEnumerable<BsonValue> values)
            {
                return In(field, new BsonArray(values));
            }

            /// <summary>
            /// Get all operands to works with array or enumerable values
            /// </summary>
            public static QueryAny Any() => new QueryAny(parameterized: true);
        }
    }
}
