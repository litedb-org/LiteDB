using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class BsonMapper
    {
        #region Deserialization Hooks

        /// <summary>
        /// Delegate for deserialization callback.
        /// </summary>
        /// <param name="sender">The BsonMapper instance that triggered the deserialization.</param>
        /// <param name="target">The target type for deserialization.</param>
        /// <param name="value">The BsonValue to be deserialized.</param>
        /// <returns>The deserialized BsonValue.</returns>
        public delegate BsonValue DeserializationCallback(BsonMapper sender, Type target, BsonValue value);

        /// <summary>
        /// Gets called before deserialization of a value
        /// </summary>
        public DeserializationCallback OnDeserialization { get; set; }

        #endregion Deserialization Hooks

        #region Basic direct .NET convert types

        // direct bson types
        private readonly HashSet<Type> _bsonTypes = new HashSet<Type>
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
        private readonly HashSet<Type> _basicTypes = new HashSet<Type>
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
        /// Deserialize a BsonValue to .NET object typed in T
        /// </summary>
        public T Deserialize<T>(BsonValue value)
        {
            if (value == null) return default(T);

            var result = this.Deserialize(typeof(T), value);

            return (T)result;
        }

        /// <summary>
        /// Deserilize a BsonValue to .NET object based on type parameter
        /// </summary>
        public object Deserialize(Type type, BsonValue value)
        {
            if (OnDeserialization is not null)
            {
                var result = OnDeserialization(this, type, value);
                if (result is not null)
                {
                    value = result;
                }
            }

            // null value - null returns
            if (value.IsNull) return null;

            // if is nullable, get underlying type
            if (Reflection.IsNullable(type))
            {
                type = Reflection.UnderlyingTypeOf(type);
            }

            // test if has a custom type implementation
            if (_customDeserializer.TryGetValue(type, out Func<BsonValue, object> custom))
            {
                return custom(value);
            }

            var typeInfo = type.GetTypeInfo();

            // check if your type is already a BsonValue/BsonDocument/BsonArray
            if (type == typeof(BsonValue))
            {
                return value;
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
                return unchecked((UInt64)value.AsInt64);
            }

            // enum value is an int
            else if (typeInfo.IsEnum)
            {
                if (value.IsString) return Enum.Parse(type, value.AsString);

                if (value.IsNumber) return Enum.ToObject(type, value.AsInt32);
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
                    var actualType = _typeNameBinder.GetType(typeField.AsString);

                    if (actualType == null) throw LiteException.InvalidTypedName(typeField.AsString);

                    // avoid initialize class that are not assignable 
                    if (!type.IsAssignableFrom(actualType))
                    {
                        throw LiteException.DataTypeNotAssignable(type.FullName, actualType.FullName);
                    }

                    type = actualType;
                }
                // when complex type has no definition (== typeof(object)) use Dictionary<string, object> to better set values
                else if (type == typeof(object))
                {
                    type = typeof(Dictionary<string, object>);
                }

                var entity = this.GetEntityMapper(type);
                entity.WaitForInitialization();

                // initialize CreateInstance
                if (entity.CreateInstance == null)
                {
                    entity.CreateInstance =
                        this.GetTypeCtor(entity) ??
                        ((BsonDocument v) => Reflection.CreateInstance(entity.ForType));
                }

                var o = _typeInstantiator(type) ?? entity.CreateInstance(doc);

                if (o is IDictionary dict)
                {
                    if (o.GetType().GetTypeInfo().IsGenericType)
                    {
                        var k = type.GetGenericArguments()[0];
                        var t = type.GetGenericArguments()[1];

                        this.DeserializeDictionary(k, t, dict, value.AsDocument);
                    }
                    else
                    {
                        this.DeserializeDictionary(typeof(object), typeof(object), dict, value.AsDocument);
                    }
                }
                else
                {
                    this.DeserializeObject(entity, o, doc);
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

            if (enumerable is IList list)
            {
                foreach (BsonValue item in value)
                {
                    list.Add(this.Deserialize(itemType, item));
                }
            }
            else
            {
                var addMethod = type.GetMethod("Add", new Type[] { itemType });

                foreach (BsonValue item in value)
                {
                    addMethod.Invoke(enumerable, new[] { this.Deserialize(itemType, item) });
                }
            }

            return enumerable;
        }

        private void DeserializeDictionary(Type K, Type T, IDictionary dict, BsonDocument value)
        {
            var isKEnum = K.GetTypeInfo().IsEnum;
            foreach (var el in value.GetElements())
            {
                var k = isKEnum ? Enum.Parse(K, el.Key) : K == typeof(Uri) ? new Uri(el.Key) : Convert.ChangeType(el.Key, K);
                var v = this.Deserialize(T, el.Value);

                dict.Add(k, v);
            }
        }

        private void DeserializeObject(EntityMapper entity, object obj, BsonDocument value)
        {
            foreach (var member in entity.Members.Where(x => x.Setter != null))
            {
                if (value.TryGetValue(member.FieldName, out var val))
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

                //  if name is Id and arg is null, look for _id
                if (arg == null && StringComparer.OrdinalIgnoreCase.Equals(par.Name, "Id") && value.TryGetValue("_id", out var id))
                {
                    arg = this.Deserialize(par.ParameterType, id);
                }

                args.Add(arg);
            }

            var obj = Activator.CreateInstance(type, args.ToArray());

            return obj;
        }
    }
}
