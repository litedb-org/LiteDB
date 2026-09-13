using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        /// <summary>
        /// Parse a JSON string into a new BsonValue
        /// JSON('{a:1}') = {a:1}
        /// </summary>
        public static BsonValue JSON(BsonValue json)
        {
            if (json.IsString)
            {
                BsonValue value = null;
                var isJson = false;

                try
                {
                    value = JsonSerializer.Deserialize(json.AsString);
                    isJson = true;
                }
                catch (LiteException ex) when (ex.ErrorCode == LiteException.UNEXPECTED_TOKEN)
                {
                }

                if (isJson) return value;
            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Create a new document and copy all properties from source document. Then copy properties (overritting if need) extend document
        /// Always returns a new document!
        /// EXTEND($, {a: 2}) = {_id:1, a: 2}
        /// </summary>
        public static BsonValue EXTEND(BsonValue source, BsonValue extend)
        {
            if (source.IsDocument && extend.IsDocument)
            {
                // make a copy of source document
                var newDoc = new BsonDocument();

                source.AsDocument.CopyTo(newDoc);
                extend.AsDocument.CopyTo(newDoc);

                // copy rawId from source
                newDoc.RawId = source.AsDocument.RawId;

                return newDoc;
            }
            else if (source.IsDocument) return source;
            else if (extend.IsDocument) return extend;

            return new BsonDocument();
        }

        /// <summary>
        /// Convert an array into IEnuemrable of values - If not array, returns as single yield value
        /// ITEMS([1, 2, null]) = 1, 2, null
        /// </summary>
        public static IEnumerable<BsonValue> ITEMS(BsonValue array)
        {
            if (array.IsArray)
            {
                foreach (var value in array.AsArray)
                {
                    yield return value;
                }
            }
            else if (array.IsBinary)
            {
                foreach (var value in array.AsBinary)
                {
                    yield return (int)value;
                }
            }
            else
            {
                yield return array;
            }
        }

        /// <summary>
        /// Concatenates 2 sequences into a new single sequence
        /// </summary>
        public static IEnumerable<BsonValue> CONCAT(IEnumerable<BsonValue> first, IEnumerable<BsonValue> second)
        {
            return first.Concat(second);
        }

        /// <summary>
        /// Get all KEYS names from a document
        /// </summary>
        public static IEnumerable<BsonValue> KEYS(BsonValue document)
        {
            if (document.IsDocument)
            {
                foreach (var key in document.AsDocument.Keys)
                {
                    yield return key;
                }
            }
        }

        /// <summary>
        /// Get all values from a document
        /// </summary>
        public static IEnumerable<BsonValue> VALUES(BsonValue document)
        {
            if (document.IsDocument)
            {
                foreach (var value in document.AsDocument.Values)
                {
                    yield return value;
                }
            }
        }

        /// <summary>
        /// Return CreationTime from ObjectId value - returns null if not an ObjectId
        /// </summary>
        public static BsonValue OID_CREATIONTIME(BsonValue objectId)
        {
            if (objectId.IsObjectId)
            {
                return objectId.AsObjectId.CreationTime;
            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Conditional IF statment. If condition are true, returns TRUE value, otherwise, FALSE value
        /// </summary>
        public static BsonValue IIF(BsonValue test, BsonValue ifTrue, BsonValue ifFalse)
        {
            // this method are not implemented because will use "Expression.Conditional"
            // will execute "ifTrue" only if test = true and will execute "ifFalse" if test = false
            throw new NotImplementedException();
        }

        /// <summary>
        /// Return first values if not null. If null, returns second value.
        /// </summary>
        public static BsonValue COALESCE(BsonValue left, BsonValue right)
        {
            return left.IsNull ? right : left;
        }

        /// <summary>
        /// Return length of variant value (valid only for String, Binary, Array or Document [keys])
        /// </summary>
        public static BsonValue LENGTH(BsonValue value)
        {
            if (value.IsString) return value.AsString.Length;
            else if (value.IsBinary) return value.AsBinary.Length;
            else if (value.IsArray) return value.AsArray.Count;
            else if (value.IsDocument) return value.AsDocument.Keys.Count;
            else if (value.IsNull) return 0;

            return BsonValue.Null;
        }

        /// <summary>
        /// Returns the first num elements of values.
        /// </summary>
        public static IEnumerable<BsonValue> TOP(IEnumerable<BsonValue> values, BsonValue num)
        {
            if (num.IsInt32 || num.IsInt64)
            {
                var numInt = num.AsInt32;

                if (numInt > 0)
                    return values.Take(numInt);
            }
            return Enumerable.Empty<BsonValue>();
        }

        /// <summary>
        /// Returns the union of the two enumerables.
        /// </summary>
        public static IEnumerable<BsonValue> UNION(IEnumerable<BsonValue> left, IEnumerable<BsonValue> right)
        {
            return left.Union(right);
        }

        /// <summary>
        /// Returns the set difference between the two enumerables.
        /// </summary>
        public static IEnumerable<BsonValue> EXCEPT(IEnumerable<BsonValue> left, IEnumerable<BsonValue> right)
        {
            return left.Except(right);
        }

        /// <summary>
        /// Returns a unique list of items
        /// </summary>
        public static IEnumerable<BsonValue> DISTINCT(IEnumerable<BsonValue> items)
        {
            return items.Distinct();
        }

        private static readonly Random _random = new Random();

        /// <summary>
        /// Return a random int value
        /// </summary>
        [Volatile]
        public static BsonValue RANDOM()
        {
            return _random.Next();
        }

        /// <summary>
        /// Return a ranom int value inside this min/max values
        /// </summary>
        [Volatile]
        public static BsonValue RANDOM(BsonValue min, BsonValue max)
        {
            if (min.IsNumber && max.IsNumber)
            {
                return _random.Next(min.AsInt32, max.AsInt32);
            }
            else
            {
                return BsonValue.Null;
            }
        }
    }
}
