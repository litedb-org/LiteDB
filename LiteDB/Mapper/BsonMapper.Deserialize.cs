using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LiteDB
{
    public partial class BsonMapper
    {
        /// <summary>
        /// Deserialize a BsonDocument to entity class
        /// </summary>
        public virtual object ToObject(Type type, BsonDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            // if T is BsonDocument, just return them
            if (type == typeof(BsonDocument)) return doc;

            return this.Deserialize(type, doc);
        }

        /// <summary>
        /// Deserialize a BsonDocument to entity class
        /// </summary>
        public virtual T ToObject<T>(BsonDocument doc)
        {
            return (T)this.ToObject(typeof(T), doc);
        }

        /// <summary>
        /// Deserialize an BsonValue to .NET object typed in T
        /// </summary>
        internal T Deserialize<T>(BsonValue value)
        {
            if (value == null) return default(T);

            var result = this.Deserialize(typeof(T), value);

            return (T)result;
        }

        #region Basic direct .NET convert types

        // direct bson types
        private HashSet<Type> _bsonTypes = new HashSet<Type>
        {
            typeof(String),
            typeof(Int32),
            typeof(Int64),
            typeof(Boolean),
            typeof(Guid),
            typeof(DateTime),
            typeof(Byte[]),
            typeof(ObjectId),
            typeof(Double),
            typeof(Decimal)
        };

        // simple convert types
        private HashSet<Type> _basicTypes = new HashSet<Type>
        {
            typeof(Int16),
            typeof(UInt16),
            typeof(UInt32),
            typeof(Single),
            typeof(Char),
            typeof(Byte),
            typeof(SByte)
        };

        #endregion

        internal object Deserialize(Type type, BsonValue value)
        {
            Func<BsonValue, object> custom;

            // null value - null returns
            if (value.IsNull) return null;

            // if is nullable, get underlying type
            else if (Reflection.IsNullable(type))
            {
                type = Reflection.UnderlyingTypeOf(type);
            }

            // check if your type is already a BsonValue/BsonDocument/BsonArray
            if (type == typeof(BsonValue))
            {
                return new BsonValue(value);
            }
            else if (type == typeof(BsonDocument))
            {
                return value.AsDocument;
            }
            else if (type == typeof(BsonArray))
            {
                return value.AsArray;
            }

            // raw values to native bson values
            else if (_bsonTypes.Contains(type))
            {
                return value.RawValue;
            }

            // simple ConvertTo to basic .NET types
            else if (_basicTypes.Contains(type))
            {
                return Convert.ChangeType(value.RawValue, type);
            }

            // special cast to UInt64 to Int64
            else if (type == typeof(UInt64))
            {
                return unchecked((UInt64)((Int64)value.RawValue));
            }

            // enum value is an int
            else if (type.GetTypeInfo().IsEnum)
            {
                return Enum.Parse(type, value.AsString);
            }

            // test if has a custom type implementation
            else if (_customDeserializer.TryGetValue(type, out custom))
            {
                return custom(value);
            }

            // if value is array, deserialize as array
            else if (value.IsArray)
            {
                // when array are from an object (like in Dictionary<string, object> { ["array"] = new string[] { "a", "b" } 
                if (type == typeof(object))
                {
                    return this.DeserializeArray(typeof(object), value.AsArray);
                }
                if (type.IsArray)
                {
                    return this.DeserializeArray(type.GetElementType(), value.AsArray);
                }
                else
                {
                    return this.DeserializeList(type, value.AsArray);
                }
            }

            // if value is document, deserialize as document
            else if (value.IsDocument)
            {
                // if type is anonymous use special handler
                if (type.IsAnonymousType())
                {
                    return this.DeserializeAnonymousType(type, value.AsDocument);
                }

                var doc = value.AsDocument;

                // test if value is object and has _type
                if (doc.TryGetValue("_type", out var typeField) && typeField.IsString)
                {
                    var actualType = Type.GetType(typeField.AsString);

                    if (actualType == null) throw LiteException.InvalidTypedName(typeField.AsString);

                    // avoid initialize class that are not assignable 
                    if (!type.IsAssignableFrom(actualType))
                    {
                        throw LiteException.DataTypeNotAssignable(type.FullName, actualType.FullName);
                    }

                    // avoid use of "System.Diagnostics.Process" in object type definition
                    // using String test to work in .netstandard 1.3
                    if (actualType.FullName.Equals("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase))
                    {
                        throw LiteException.AvoidUseOfProcess();
                    }

                    type = actualType;
                }
                // when complex type has no definition (== typeof(object)) use Dictionary<string, object> to better set values
                else if (type == typeof(object))
                {
                    type = typeof(Dictionary<string, object>);
                }

                var o = _typeInstantiator(type);

                if (o is IDictionary && type.GetTypeInfo().IsGenericType)
                {
                    var k = type.GetTypeInfo().GetGenericArguments()[0];
                    var t = type.GetTypeInfo().GetGenericArguments()[1];

                    this.DeserializeDictionary(k, t, (IDictionary)o, value.AsDocument);
                }
                else
                {
                    this.DeserializeObject(type, o, doc);
                }

                return o;
            }

            // in last case, return value as-is - can cause "cast error"
            // it's used for "public object MyInt { get; set; }"
            return value.RawValue;
        }

        private object DeserializeArray(Type type, BsonArray array)
        {
            var arr = Array.CreateInstance(type, array.Count);
            var idx = 0;

            foreach (var item in array)
            {
                arr.SetValue(this.Deserialize(type, item), idx++);
            }

            return arr;
        }

        private object DeserializeList(Type type, BsonArray value)
        {
            var itemType = Reflection.GetListItemType(type);
            var enumerable = (IEnumerable)Reflection.CreateInstance(type);
            var list = enumerable as IList;

            if (list != null)
            {
                foreach (BsonValue item in value)
                {
                    list.Add(Deserialize(itemType, item));
                }
            }
            else
            {
                var addMethod = type.GetMethod("Add");

                foreach (BsonValue item in value)
                {
                    addMethod.Invoke(enumerable, new[] { Deserialize(itemType, item) });
                }
            }

            return enumerable;
        }

        private void DeserializeDictionary(Type K, Type T, IDictionary dict, BsonDocument value)
        {
            foreach (var key in value.Keys)
            {
                var k = K.GetTypeInfo().IsEnum ? Enum.Parse(K, key) : Convert.ChangeType(key, K);
                var v = this.Deserialize(T, value[key]);

                dict.Add(k, v);
            }
        }

        private void DeserializeObject(Type type, object obj, BsonDocument value)
        {
            var entity = this.GetEntityMapper(type);

            foreach (var member in entity.Members.Where(x => x.Setter != null))
            {
                var val = value[member.FieldName];

                if (!val.IsNull)
                {
                    // check if has a custom deserialize function
                    if (member.Deserialize != null)
                    {
                        member.Setter(obj, member.Deserialize(val, this));
                    }
                    else
                    {
                        member.Setter(obj, this.Deserialize(member.DataType, val));
                    }
                }
            }
        }

        private object DeserializeAnonymousType(Type type, BsonDocument value)
        {
            var args = new List<object>();
            var ctor = type.GetConstructors()[0];

            foreach (var par in ctor.GetParameters())
            {
                var arg = this.Deserialize(par.ParameterType, value[par.Name]);

                args.Add(arg);
            }

            var obj = Activator.CreateInstance(type, args.ToArray());

            return obj;
        }
    }
}