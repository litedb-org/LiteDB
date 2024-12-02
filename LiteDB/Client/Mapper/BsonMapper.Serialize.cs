using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace LiteDB
{
    public partial class BsonMapper
    {
        /// <summary>
        /// Serialize a entity class to BsonDocument
        /// </summary>
        public virtual BsonDocument ToDocument(Type type, object entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            // if object is BsonDocument, just return them
            if (entity is BsonDocument) return (BsonDocument)(object)entity;

            return this.Serialize(type, entity, 0).AsDocument;
        }

        /// <summary>
        /// Serialize a entity class to BsonDocument
        /// </summary>
        public virtual BsonDocument ToDocument<T>(T entity)
        {
            return this.ToDocument(typeof(T), entity)?.AsDocument;
        }

        /// <summary>
        /// Serialize to BsonValue any .NET object based on T type (using mapping rules)
        /// </summary>
        public BsonValue Serialize<T>(T obj)
        {
            return this.Serialize(typeof(T), obj, 0);
        }

        /// <summary>
        /// Serialize to BsonValue any .NET object based on type parameter (using mapping rules)
        /// </summary>
        public BsonValue Serialize(Type type, object obj)
        {
            return this.Serialize(type, obj, 0);
        }

        internal BsonValue Serialize(Type type, object obj, int depth)
        {
            if (++depth > MaxDepth) throw LiteException.DocumentMaxDepth(MaxDepth, type);

            if (obj == null) return BsonValue.Null;

            // if is already a bson value
            if (obj is BsonValue bsonValue) return bsonValue;

            // check if is a custom type
            else if (_customSerializer.TryGetValue(type, out var custom) || _customSerializer.TryGetValue(obj.GetType(), out custom))
            {
                return custom(obj);
            }
            // test string - mapper has some special options
            else if (obj is String)
            {
                var str = this.TrimWhitespace ? (obj as String).Trim() : (String)obj;

                if (this.EmptyStringToNull && str.Length == 0)
                {
                    return BsonValue.Null;
                }
                else
                {
                    return new BsonValue(str);
                }
            }
            // basic Bson data types (cast datatype for better performance optimization)
            else if (obj is Int32) return new BsonValue((Int32)obj);
            else if (obj is Int64) return new BsonValue((Int64)obj);
            else if (obj is Double) return new BsonValue((Double)obj);
            else if (obj is Decimal) return new BsonValue((Decimal)obj);
            else if (obj is Byte[]) return new BsonValue((Byte[])obj);
            else if (obj is ObjectId) return new BsonValue((ObjectId)obj);
            else if (obj is Guid) return new BsonValue((Guid)obj);
            else if (obj is Boolean) return new BsonValue((Boolean)obj);
            else if (obj is DateTime) return new BsonValue((DateTime)obj);
            // basic .net type to convert to bson
            else if (obj is Int16 || obj is UInt16 || obj is Byte || obj is SByte)
            {
                return new BsonValue(Convert.ToInt32(obj));
            }
            else if (obj is UInt32)
            {
                return new BsonValue(Convert.ToInt64(obj));
            }
            else if (obj is UInt64)
            {
                var ulng = ((UInt64)obj);
                var lng = unchecked((Int64)ulng);

                return new BsonValue(lng);
            }
            else if (obj is Single)
            {
                return new BsonValue(Convert.ToDouble(obj));
            }
            else if (obj is Char)
            {
                return new BsonValue(obj.ToString());
            }
            else if (obj is Enum)
            {
                if (EnumAsInteger)
                {
                    return new BsonValue((int)obj);
                }
                else
                {
                    return new BsonValue(obj.ToString());
                }
            }
            // for dictionary
            else if (obj is IDictionary dict)
            {
                // when you are converting Dictionary<string, object>
                if (type == typeof(object))
                {
                    type = obj.GetType();
                }

                Type valueType = typeof(object);

                if (type.GetTypeInfo().IsGenericType) {
                    Type[] generics = type.GetGenericArguments();
                    valueType = generics[1];
                }

                return SerializeDictionary(valueType, dict, depth);
            }
            // check if is a list or array
            else if (obj is IEnumerable)
            {
                return SerializeArray(Reflection.GetListItemType(type), obj as IEnumerable, depth);
            }
            // otherwise serialize as a plain object
            else
            {
                return SerializeObject(type, obj, depth);
            }
        }

        private BsonArray SerializeArray(Type type, IEnumerable array, int depth)
        {
            BsonArray bsonArray = [];

            foreach (object item in array)
            {
                bsonArray.Add(Serialize(type, item, depth));
            }

            return bsonArray;
        }

        private BsonDocument SerializeDictionary(Type valueType, IDictionary dict, int depth)
        {
            BsonDocument bsonDocument = [];

            foreach (DictionaryEntry entry in dict)
            {
                string stringKey = entry.Key as string
                    // Serialize key as JSON to support any key type
                    ?? JsonSerializer.Serialize(Serialize(entry.Key));

                BsonValue bsonValue = Serialize(valueType, entry.Value, depth);

                bsonDocument[stringKey] = bsonValue;
            }

            return bsonDocument;
        }

        private BsonDocument SerializeObject(Type type, object obj, int depth)
        {
            var t = obj.GetType();
            var doc = new BsonDocument();
            var entity = this.GetEntityMapper(t);
            entity.WaitForInitialization();

            // adding _type only where property Type is not same as object instance type
            if (type != t)
            {
                doc["_type"] = new BsonValue(_typeNameBinder.GetName(t));
            }

            foreach (var member in entity.Members.Where(x => x.Getter != null))
            {
                // get member value
                var value = member.Getter(obj);

                if (value == null && this.SerializeNullValues == false && member.FieldName != "_id") continue;

                // if member has a custom serialization, use it
                if (member.Serialize != null)
                {
                    doc[member.FieldName] = member.Serialize(value, this);
                }
                else
                {
                    doc[member.FieldName] = this.Serialize(member.DataType, value, depth);
                }
            }

            return doc;
        }
    }
}