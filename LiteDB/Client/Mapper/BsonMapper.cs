using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Provides mapping functionality to convert entity classes to and from <see cref="BsonDocument"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For optimal performance when using a custom instance (instead of <see cref="Global"/>), cache the instance for reuse.
    /// </para>
    /// <para>Serialization requirements:</para>
    /// <list type="bullet">
    /// <item><description>Classes must be public with a public parameterless constructor.</description></item>
    /// <item><description>Properties must have a public getter (can be read-only).</description></item>
    /// <item><description>Entity classes must have an Id property, [ClassName]Id property, or <c>[BsonId]</c> attribute.</description></item>
    /// <item><description>Circular references are not supported.</description></item>
    /// <item><description>Fields are not serialized by default (use <see cref="IncludeFields"/> to enable).</description></item>
    /// <item><description><see cref="IList"/> and arrays are supported.</description></item>
    /// <item><description><see cref="IDictionary"/> is supported (keys must be simple data types convertible via <see cref="Convert.ChangeType(object, Type)"/>).</description></item>
    /// </list>
    /// </remarks>
    public partial class BsonMapper
    {
        #region Properties
        
        /// <summary>
        /// Provides a thread-safe mapping of types to custom serialization functions for converting objects to
        /// BsonValue instances.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Func<object, BsonValue>> _customSerializer = new ConcurrentDictionary<Type, Func<object, BsonValue>>();
        
        /// <summary>
        /// Provides a thread-safe mapping of types to custom deserializer functions for converting BsonValue instances
        /// to objects of the specified type.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Func<BsonValue, object>> _customDeserializer = new ConcurrentDictionary<Type, Func<BsonValue, object>>();

        /// <summary>
        /// Type instantiator function to support IoC
        /// </summary>
        private readonly Func<Type, object> _typeInstantiator;

        /// <summary>
        /// Type name binder to control how type names are serialized to BSON documents
        /// </summary>
        private readonly ITypeNameBinder _typeNameBinder;

        /// <summary>
        /// Gets the global <see cref="BsonMapper"/> instance used when no custom mapper is provided to the <see cref="LiteDatabase"/> constructor.
        /// </summary>
        public static BsonMapper Global = new BsonMapper();

        /// <summary>
        /// Gets or sets the field name resolver function that transforms property names to field names.
        /// </summary>
        public Func<string, string> ResolveFieldName;

        /// <summary>
        /// Gets or sets whether the mapper should serialize <see langword="null"/> values.
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool SerializeNullValues { get; set; }

        /// <summary>
        /// Gets or sets whether to apply <see cref="string.Trim()"/> to strings during serialization.
        /// <para>Default is <see langword="true"/>.</para>
        /// </summary>
        public bool TrimWhitespace { get; set; }

        /// <summary>
        /// Gets or sets whether to convert empty strings to <see langword="null"/> during serialization.
        /// <para>Default is <see langword="true"/>.</para>
        /// </summary>
        public bool EmptyStringToNull { get; set; }

        /// <summary>
        /// Gets or sets whether enums should be converted to integer values. If <see langword="false"/>, enums are converted to strings.
        /// <para>Must be <see langword="true"/> to support LINQ expressions.</para>
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool EnumAsInteger { get; set; }

        /// <summary>
        /// Gets or sets whether the mapper should include fields in serialization.
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool IncludeFields { get; set; }

        /// <summary>
        /// Gets or sets whether the mapper should include non-public members (private, protected, and internal).
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool IncludeNonPublic { get; set; }

        /// <summary>
        /// Gets or sets the maximum depth for nested object serialization.
        /// <para>Default is 20.</para>
        /// </summary>
        public int MaxDepth { get; set; }

        /// <summary>
        /// Gets or sets a custom callback to modify <see cref="MemberMapper"/> behavior when converting <see cref="MemberInfo"/> to field mappings.
        /// </summary>
        /// <remarks>
        /// Set <see cref="MemberMapper.FieldName"/> to <see langword="null"/> to exclude a member from the mapped document.
        /// </remarks>
        public Action<Type, MemberInfo, MemberMapper> ResolveMember;

        /// <summary>
        /// Gets or sets a custom function to resolve collection names based on entity type.
        /// </summary>
        public Func<Type, string> ResolveCollectionName;

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="BsonMapper"/> class.
        /// </summary>
        /// <param name="customTypeInstantiator">Optional custom type instantiator function for IoC support.</param>
        /// <param name="typeNameBinder">Optional custom type name binder for controlling type name serialization.</param>
        public BsonMapper(Func<Type, object> customTypeInstantiator = null, ITypeNameBinder typeNameBinder = null)
        {
            this.SerializeNullValues = false;
            this.TrimWhitespace = true;
            this.EmptyStringToNull = true;
            this.EnumAsInteger = false;
            this.ResolveFieldName = (s) => s;
            this.ResolveMember = (t, mi, mm) => { };
            this.ResolveCollectionName = (t) => Reflection.IsEnumerable(t) ? Reflection.GetListItemType(t).Name : t.Name;
            this.IncludeFields = false;
            this.MaxDepth = 20;

            _typeInstantiator = customTypeInstantiator ?? ((Type t) => null);
            _typeNameBinder = typeNameBinder ?? DefaultTypeNameBinder.Instance;

            #region Register CustomTypes

            RegisterType<Uri>(uri => uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.ToString(), bson => new Uri(bson.AsString));
            RegisterType<DateTimeOffset>(value => new BsonValue(value.UtcDateTime), bson => bson.AsDateTime.ToUniversalTime());
            RegisterType<TimeSpan>(value => new BsonValue(value.Ticks), bson => new TimeSpan(bson.AsInt64));
            RegisterType<Regex>(
                r => r.Options == RegexOptions.None ? new BsonValue(r.ToString()) : new BsonDocument { { "p", r.ToString() }, { "o", (int)r.Options } },
                value => value.IsString ? new Regex(value) : new Regex(value.AsDocument["p"].AsString, (RegexOptions)value.AsDocument["o"].AsInt32)
            );


            #endregion

        }

        #region Register CustomType

        /// <summary>
        /// Registers custom serialization and deserialization functions for a specific type.
        /// </summary>
        /// <typeparam name="T">The type to register custom serialization for.</typeparam>
        /// <param name="serialize">Function to convert type <typeparamref name="T"/> to <see cref="BsonValue"/>.</param>
        /// <param name="deserialize">Function to convert <see cref="BsonValue"/> back to type <typeparamref name="T"/>.</param>
        public void RegisterType<T>(Func<T, BsonValue> serialize, Func<BsonValue, T> deserialize)
        {
            _customSerializer[typeof(T)] = (o) => serialize((T)o);
            _customDeserializer[typeof(T)] = (b) => (T)deserialize(b);
        }

        /// <summary>
        /// Registers custom serialization and deserialization functions for a specific type.
        /// </summary>
        /// <param name="type">The type to register custom serialization for.</param>
        /// <param name="serialize">Function to convert the type to <see cref="BsonValue"/>.</param>
        /// <param name="deserialize">Function to convert <see cref="BsonValue"/> back to the type.</param>
        public void RegisterType(Type type, Func<object, BsonValue> serialize, Func<BsonValue, object> deserialize)
        {
            _customSerializer[type] = (o) => serialize(o);
            _customDeserializer[type] = (b) => deserialize(b);
        }

        #endregion

        /// <summary>
        /// Creates a fluent API entity builder for configuring how type <typeparamref name="T"/> is mapped to BSON documents.
        /// </summary>
        /// <typeparam name="T">The entity type to configure.</typeparam>
        /// <returns>An <see cref="EntityBuilder{T}"/> instance for fluent configuration.</returns>
        public EntityBuilder<T> Entity<T>()
        {
            return new EntityBuilder<T>(this, _typeNameBinder);
        }

        #region Get LinqVisitor processor

        /// <summary>
        /// Resolves a LINQ expression into a <see cref="BsonExpression"/>.
        /// </summary>
        /// <typeparam name="T">The source entity type.</typeparam>
        /// <typeparam name="K">The result type of the expression.</typeparam>
        /// <param name="predicate">The LINQ expression to resolve.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the LINQ expression.</returns>
        public BsonExpression GetExpression<T, K>(Expression<Func<T, K>> predicate)
        {
            var visitor = new LinqExpressionVisitor(this, predicate);

            var expr = visitor.Resolve(typeof(K) == typeof(bool));

            LOG($"`{predicate.ToString()}` -> `{expr.Source}`", "LINQ");

            return expr;
        }

        /// <summary>
        /// Resolves a LINQ expression into a <see cref="BsonExpression"/> for index creation.
        /// </summary>
        /// <typeparam name="T">The source entity type.</typeparam>
        /// <typeparam name="K">The result type of the expression.</typeparam>
        /// <param name="predicate">The LINQ expression to resolve.</param>
        /// <returns>A <see cref="BsonExpression"/> representing the index expression.</returns>
        public BsonExpression GetIndexExpression<T, K>(Expression<Func<T, K>> predicate)
        {
            var visitor = new LinqExpressionVisitor(this, predicate);

            var expr = visitor.Resolve(false);

            LOG($"`{predicate.ToString()}` -> `{expr.Source}`", "LINQ");

            return expr;
        }

        #endregion

        #region Predefinded Property Resolvers

        /// <summary>
        /// Configures the mapper to use lower camel case for converting property names to field names.
        /// </summary>
        /// <returns>The current <see cref="BsonMapper"/> instance for method chaining.</returns>
        /// <example>
        /// <c>PropertyName</c> becomes <c>propertyName</c>.
        /// </example>
        public BsonMapper UseCamelCase()
        {
            this.ResolveFieldName = (s) => char.ToLower(s[0]) + s.Substring(1);

            return this;
        }

        private readonly Regex _lowerCaseDelimiter = new Regex("(?!(^[A-Z]))([A-Z])", RegexOptions.Compiled);

        /// <summary>
        /// Configures the mapper to use lower case with a delimiter for converting property names to field names.
        /// </summary>
        /// <param name="delimiter">The delimiter character to use between words. Default is underscore (<c>_</c>).</param>
        /// <returns>The current <see cref="BsonMapper"/> instance for method chaining.</returns>
        /// <example>
        /// With default delimiter: <c>PropertyName</c> becomes <c>property_name</c>.
        /// </example>
        public BsonMapper UseLowerCaseDelimiter(char delimiter = '_')
        {
            this.ResolveFieldName = (s) => _lowerCaseDelimiter.Replace(s, delimiter + "$2").ToLower();

            return this;
        }

        #endregion

        #region Register DbRef

        /// <summary>
        /// Register a property mapper as DbRef to serialize/deserialize only document reference _id
        /// </summary>
        internal static void RegisterDbRef(BsonMapper mapper, MemberMapper member, ITypeNameBinder typeNameBinder, string collection)
        {
            member.IsDbRef = true;

            if (member.IsEnumerable)
            {
                RegisterDbRefList(mapper, member, typeNameBinder, collection);
            }
            else
            {
                RegisterDbRefItem(mapper, member, typeNameBinder, collection);
            }
        }

        /// <summary>
        /// Register a property as a DbRef - implement a custom Serialize/Deserialize actions to convert entity to $id, $ref only
        /// </summary>
        private static void RegisterDbRefItem(BsonMapper mapper, MemberMapper member, ITypeNameBinder typeNameBinder, string collection)
        {
            // get entity
            var entity = mapper.GetEntityMapper(member.DataType);
            
            member.Serialize = (obj, m) =>
            {
                // supports null values when "SerializeNullValues = true"
                if (obj == null) return BsonValue.Null;
                entity.WaitForInitialization();
                
                var idField = entity.Id;

                // #768 if using DbRef with interface with no ID mapped
                if (idField == null) throw new LiteException(0, "There is no _id field mapped in your type: " + member.DataType.FullName);

                var id = idField.Getter(obj);

                var bsonDocument = new BsonDocument
                {
                    ["$id"] = m.Serialize(id.GetType(), id, 0),
                    ["$ref"] = collection
                };

                if (member.DataType != obj.GetType())
                {
                    bsonDocument["$type"] = typeNameBinder.GetName(obj.GetType());
                }

                return bsonDocument;
            };

            member.Deserialize = (bson, m) =>
            {
                // if not a document (maybe BsonValue.null) returns null
                if (bson == null || bson.IsDocument == false) return null;

                var doc = bson.AsDocument;
                var idRef = doc["$id"];
                var missing = doc["$missing"] == true;
                var included = doc.ContainsKey("$ref") == false;

                if (missing) return null;

                if (included)
                {
                    doc["_id"] = idRef;
                    if (doc.ContainsKey("$type"))
                    {
                        doc["_type"] = bson["$type"];
                    }

                    return m.Deserialize(entity.ForType, doc);

                }
                else
                {
                    return m.Deserialize(entity.ForType,
                        doc.ContainsKey("$type") ?
                            new BsonDocument { ["_id"] = idRef, ["_type"] = bson["$type"] } :
                            new BsonDocument { ["_id"] = idRef }); // if has $id, deserialize object using only _id object
                }

            };
        }

        /// <summary>
        /// Register a property as a DbRefList - implement a custom Serialize/Deserialize actions to convert entity to $id, $ref only
        /// </summary>
        private static void RegisterDbRefList(BsonMapper mapper, MemberMapper member, ITypeNameBinder typeNameBinder, string collection)
        {
            // get entity from list item type
            var entity = mapper.GetEntityMapper(member.UnderlyingType);

            member.Serialize = (list, m) =>
            {
                // supports null values when "SerializeNullValues = true"
                if (list == null) return BsonValue.Null;
                entity.WaitForInitialization();
                
                var result = new BsonArray();
                var idField = entity.Id;

                foreach (var item in (IEnumerable)list)
                {
                    if (item == null) continue;

                    var id = idField.Getter(item);

                    var bsonDocument = new BsonDocument
                    {
                        ["$id"] = m.Serialize(id.GetType(), id, 0),
                        ["$ref"] = collection
                    };

                    if (member.UnderlyingType != item.GetType())
                    {
                        bsonDocument["$type"] = typeNameBinder.GetName(item.GetType());
                    }

                    result.Add(bsonDocument);
                }

                return result;
            };

            member.Deserialize = (bson, m) =>
            {
                if (bson.IsArray == false) return null;

                var array = bson.AsArray;

                if (array.Count == 0) return m.Deserialize(member.DataType, array);

                // copy array changing $id to _id
                var result = new BsonArray();

                foreach (var item in array)
                {
                    if (item.IsDocument == false) continue;

                    var doc = item.AsDocument;
                    var idRef = doc["$id"];
                    var missing = doc["$missing"] == true;
                    var included = doc.ContainsKey("$ref") == false;

                    // if referece document are missing, do not inlcude on output list
                    if (missing) continue;

                    // if refId is null was included by "include" query, so "item" is full filled document
                    if (included)
                    {
                        item["_id"] = idRef;
                        if (item.AsDocument.ContainsKey("$type"))
                        {
                            item["_type"] = item["$type"];
                        }

                        result.Add(item);
                    }
                    else
                    {
                        var bsonDocument = new BsonDocument { ["_id"] = idRef };

                        if (item.AsDocument.ContainsKey("$type"))
                        {
                            bsonDocument["_type"] = item["$type"];
                        }

                        result.Add(bsonDocument);
                    }

                }

                return m.Deserialize(member.DataType, result);
            };
        }

        #endregion
    }
}