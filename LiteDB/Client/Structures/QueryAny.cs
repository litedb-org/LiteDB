using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public class QueryAny
    {
        private readonly bool _parameterized;

        /// <summary>Creates the original literal-based ANY helpers.</summary>
        public QueryAny() { }

        internal QueryAny(bool parameterized) { _parameterized = parameterized; }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields is equal to value
        /// </summary>
        public BsonExpression EQ(string arrayField, BsonValue value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY = {0}", value);
            return BsonExpression.Create($"{arrayField} ANY = {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields are less tha to value (&lt;)
        /// </summary>
        public BsonExpression LT(string arrayField, BsonValue value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY < {0}", value);
            return BsonExpression.Create($"{arrayField} ANY < {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields are less than or equals value (&lt;=)
        /// </summary>
        public BsonExpression LTE(string arrayField, BsonValue value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY <= {0}", value);
            return BsonExpression.Create($"{arrayField} ANY <= {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields are greater than value (&gt;)
        /// </summary>
        public BsonExpression GT(string arrayField, BsonValue value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY > {0}", value);
            return BsonExpression.Create($"{arrayField} ANY > {value ?? BsonValue.Null}");

        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields are greater than or equals value (&gt;=)
        /// </summary>
        public BsonExpression GTE(string arrayField, BsonValue value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY >= {0}", value);
            return BsonExpression.Create($"{arrayField} ANY >= {value ?? BsonValue.Null}");
        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields are between "start" and "end" values (BETWEEN)
        /// </summary>
        public BsonExpression Between(string arrayField, BsonValue start, BsonValue end)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY BETWEEN {0} AND {1}", start, end);
            return BsonExpression.Create($"{arrayField} ANY BETWEEN {start ?? BsonValue.Null} AND {end ?? BsonValue.Null}");
        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields starts with value (LIKE)
        /// </summary>
        public BsonExpression StartsWith(string arrayField, string value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));
            if (value.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(value));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY LIKE {0}", new BsonValue(value + "%"));
            return BsonExpression.Create($"{arrayField} ANY LIKE {new BsonValue(value + "%")}");
        }
        
        /// <summary>
        /// Returns all documents for which at least one value in arrayFields ends with value (LIKE)
        /// </summary>
        public BsonExpression EndsWith(string arrayField, string value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));
            if (value.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(value));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY LIKE {0}", new BsonValue("%" + value));
            return BsonExpression.Create($"{arrayField} ANY LIKE {new BsonValue("%" + value)}");
        }

        /// <summary>
        /// Returns all documents for which at least one value in arrayFields contains the value (CONTAINS)
        /// </summary>
        public BsonExpression Contains(string arrayField, string value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));
            if (value.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(value));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY LIKE {0}", new BsonValue("%" + value + "%"));
            return BsonExpression.Create($"{arrayField} ANY LIKE {new BsonValue("%" + value + "%")}");
        }
        
        /// <summary>
        /// Returns all documents for which at least one value in arrayFields are not equals to value (not equals)
        /// </summary>
        public BsonExpression Not(string arrayField, BsonValue value)
        {
            if (arrayField.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(arrayField));

            if (_parameterized) return Query.CreateValueQuery(arrayField, "ANY != {0}", value);
            return BsonExpression.Create($"{arrayField} ANY != {value ?? BsonValue.Null}");
        }
    }
}