using System;
using System.Collections;
using System.Collections.Generic;
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

        internal BsonValue Serialize(Type type, object obj, int depth)
        {
            if (++depth > MAX_DEPTH) throw LiteException.DocumentMaxDepth(MAX_DEPTH, type);

            if (obj == null) return BsonValue.Null;

            Func<object, BsonValue> custom;

            // if is already a bson value
            if (obj is BsonValue) return new BsonValue((BsonValue)obj);

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
            else if (obj is Char || obj is Enum)
            {
                return new BsonValue(obj.ToString());
            }
            // check if is a custom type
            else if (_customSerializer.TryGetValue(type, out custom) || _customSerializer.TryGetValue(obj.GetType(), out custom))
            {
                return custom(obj);
            }
            // for dictionary
            else if (obj is IDictionary)
            {
                // when you are converting Dictionary<string, object>
                if (type == typeof(object))
                {
                    type = obj.GetType();
                }

                var itemType = type.GetTypeInfo().GetGenericArguments()[1];

                return this.SerializeDictionary(itemType, obj as IDictionary, depth);
            }
            // check if is a list or array
            else if (obj is IEnumerable)
            {
                return this.SerializeArray(Reflection.GetListItemType(obj.GetType()), obj as IEnumerable, depth);
            }
            // otherwise serialize as a plain object
            else
            {
                return this.SerializeObject(type, obj, depth);
            }
        }

        private BsonArray SerializeArray(Type type, IEnumerable array, int depth)
        {
            var arr = new BsonArray();

            foreach (var item in array)
            {
                arr.Add(this.Serialize(type, item, depth));
            }

            return arr;
        }

        private BsonDocument SerializeDictionary(Type type, IDictionary dict, int depth)
        {
            var o = new BsonDocument();

            foreach (var key in dict.Keys)
            {
                var value = dict[key];

                o.RawValue[key.ToString()] = this.Serialize(type, value, depth);
            }

            return o;
        }

        private BsonDocument SerializeObject(Type type, object obj, int depth)
        {
            var o = new BsonDocument();
            var t = obj.GetType();
            var entity = this.GetEntityMapper(t);
            var dict = o.RawValue;

            // adding _type only where property Type is not same as object instance type
            if (type != t)
            {
                dict["_type"] = new BsonValue(t.FullName + ", " + t.GetTypeInfo().Assembly.GetName().Name);
            }

            foreach (var member in entity.Members.Where(x => x.Getter != null))
            {
                // get member value
                var value = member.Getter(obj);

                if (value == null && this.SerializeNullValues == false && member.FieldName != "_id") continue;

                // if member has a custom serialization, use it
                if (member.Serialize != null)
                {
                    dict[member.FieldName] = member.Serialize(value, this);
                }
                else
                {
                    dict[member.FieldName] = this.Serialize(member.DataType, value, depth);
                }
            }

            return o;
        }
    }
}