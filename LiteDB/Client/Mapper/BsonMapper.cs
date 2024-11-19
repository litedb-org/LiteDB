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
    /// Class that converts your entity class to/from BsonDocument
    /// If you prefer use a new instance of BsonMapper (not Global), be sure cache this instance for better performance
    /// Serialization rules:
    ///     - Classes must be "public" with a public constructor (without parameters)
    ///     - Properties must have public getter (can be read-only)
    ///     - Entity class must have Id property, [ClassName]Id property or [BsonId] attribute
    ///     - No circular references
    ///     - Fields are not valid
    ///     - IList, Array supports
    ///     - IDictionary supports (Key must be a simple datatype - converted by ChangeType)
    /// </summary>
    public partial class BsonMapper
    {
        #region Properties

        /// <summary>
        /// Mapping cache between Class/BsonDocument
        /// </summary>
        private readonly Dictionary<Type, EntityMapper> _entities = new Dictionary<Type, EntityMapper>();
        
        /// <summary>
        /// Unfinished mapping cache between Class/BsonDocument
        /// </summary>
        private readonly Dictionary<Type, EntityMapper> _buildEntities = new Dictionary<Type, EntityMapper>();
        
        /// <summary>
        /// Map serializer/deserialize for custom types
        /// </summary>
        private readonly ConcurrentDictionary<Type, Func<object, BsonValue>> _customSerializer = new ConcurrentDictionary<Type, Func<object, BsonValue>>();

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
        /// Global instance used when no BsonMapper are passed in LiteDatabase ctor
        /// </summary>
        public static BsonMapper Global = new BsonMapper();

        /// <summary>
        /// A resolver name for field
        /// </summary>
        public Func<string, string> ResolveFieldName;

        /// <summary>
        /// Indicate that mapper do not serialize null values (default false)
        /// </summary>
        public bool SerializeNullValues { get; set; }

        /// <summary>
        /// Apply .Trim() in strings when serialize (default true)
        /// </summary>
        public bool TrimWhitespace { get; set; }

        /// <summary>
        /// Convert EmptyString to Null (default true)
        /// </summary>
        public bool EmptyStringToNull { get; set; }

        /// <summary>
        /// Get/Set if enum must be converted into Integer value. If false, enum will be converted into String value.
        /// MUST BE "true" to support LINQ expressions (default false)
        /// </summary>
        public bool EnumAsInteger { get; set; }

        /// <summary>
        /// Get/Set that mapper must include fields (default: false)
        /// </summary>
        public bool IncludeFields { get; set; }

        /// <summary>
        /// Get/Set that mapper must include non public (private, protected and internal) (default: false)
        /// </summary>
        public bool IncludeNonPublic { get; set; }

        /// <summary>
        /// Get/Set maximum depth for nested object (default 20)
        /// </summary>
        public int MaxDepth { get; set; }

        /// <summary>
        /// A custom callback to change MemberInfo behavior when converting to MemberMapper.
        /// Use mapper.ResolveMember(Type entity, MemberInfo property, MemberMapper documentMappedField)
        /// Set FieldName to null if you want remove from mapped document
        /// </summary>
        public Action<Type, MemberInfo, MemberMapper> ResolveMember;

        /// <summary>
        /// Custom resolve name collection based on Type 
        /// </summary>
        public Func<Type, string> ResolveCollectionName;

        #endregion

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
        /// Register a custom type serializer/deserialize function
        /// </summary>
        public void RegisterType<T>(Func<T, BsonValue> serialize, Func<BsonValue, T> deserialize)
        {
            _customSerializer[typeof(T)] = (o) => serialize((T)o);
            _customDeserializer[typeof(T)] = (b) => (T)deserialize(b);
        }

        /// <summary>
        /// Register a custom type serializer/deserialize function
        /// </summary>
        public void RegisterType(Type type, Func<object, BsonValue> serialize, Func<BsonValue, object> deserialize)
        {
            _customSerializer[type] = (o) => serialize(o);
            _customDeserializer[type] = (b) => deserialize(b);
        }

        #endregion

        /// <summary>
        /// Map your entity class to BsonDocument using fluent API
        /// </summary>
        public EntityBuilder<T> Entity<T>()
        {
            return new EntityBuilder<T>(this, _typeNameBinder);
        }

        #region Get LinqVisitor processor

        /// <summary>
        /// Resolve LINQ expression into BsonExpression
        /// </summary>
        public BsonExpression GetExpression<T, K>(Expression<Func<T, K>> predicate)
        {
            var visitor = new LinqExpressionVisitor(this, predicate);

            var expr = visitor.Resolve(typeof(K) == typeof(bool));

            LOG($"`{predicate.ToString()}` -> `{expr.Source}`", "LINQ");

            return expr;
        }

        /// <summary>
        /// Resolve LINQ expression into BsonExpression (for index only)
        /// </summary>
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
        /// Use lower camel case resolution for convert property names to field names
        /// </summary>
        public BsonMapper UseCamelCase()
        {
            this.ResolveFieldName = (s) => char.ToLower(s[0]) + s.Substring(1);

            return this;
        }

        private readonly Regex _lowerCaseDelimiter = new Regex("(?!(^[A-Z]))([A-Z])", RegexOptions.Compiled);

        /// <summary>
        /// Uses lower camel case with delimiter to convert property names to field names
        /// </summary>
        public BsonMapper UseLowerCaseDelimiter(char delimiter = '_')
        {
            this.ResolveFieldName = (s) => _lowerCaseDelimiter.Replace(s, delimiter + "$2").ToLower();

            return this;
        }

        #endregion

        #region GetEntityMapper
        
        

        /// <summary>
        /// Get property mapper between typed .NET class and BsonDocument - Cache results
        /// </summary>
        internal EntityMapper GetEntityMapper(Type type)
        {
            //TODO: needs check if Type if BsonDocument? Returns empty EntityMapper?

            if (_entities.TryGetValue(type, out EntityMapper mapper))
            {
                return mapper;
            }

            lock (_entities)
            {
                if (_entities.TryGetValue(type, out mapper))
                {
                    return mapper;
                }

                if(_buildEntities.TryGetValue(type, out EntityMapper buildMapper))
                {
                    return buildMapper;
                }
                        
                var newMapper = new EntityMapper(type);
                _buildEntities[type] = newMapper;
                this.BuildEntityMapper(newMapper);
                _entities[type] = newMapper;
                        
                _buildEntities.Remove(type);
                return newMapper;
            }

            return mapper;
        }
        
                /// <summary>
        /// Override this function to customize the BsonIgnore attribute.
        /// </summary>
        /// <param name="mi">The member info BsonIgnore attribute will be checked on.</param>
        /// <returns>Returns <see langword="true"/> if the specified ignore attribute is defined on this member, otherwise <see langword="false"/>.</returns>
        protected virtual bool CheckIgnoreAttribute(MemberInfo mi)
		{
            // Check if the ignore attribute is set on the given member.
            // Use the BsonIgnoreAttribute by default.
            return CustomAttributeExtensions.IsDefined(mi, typeof(BsonIgnoreAttribute), true);
        }

        /// <summary>
        /// Override this function to customize BsonField attribute.
        /// </summary>
        /// <param name="mi">The member info BsonField attribute will be checked on.</param>
        /// <returns>Returns the name of the field if the attribute is set. Otherwise <see langword="null"/>.</returns>
        protected virtual string CheckFieldAttribute(MemberInfo mi)
		{
            // Check if the bson field attribute is set on the given member.
            // Use the BsonFieldAttribute by default.
            BsonFieldAttribute field = (BsonFieldAttribute)CustomAttributeExtensions.GetCustomAttributes(mi, typeof(BsonFieldAttribute), true).FirstOrDefault();
            if (field != null && field.Name != null)
                return field.Name;

            return null;
        }

        /// <summary>
        /// Override this function to customize BsonId attribute.
        /// </summary>
        /// <param name="mi">The member info BsonId attribute will be checked on.</param>
        /// <returns>Returns the value of <seealso cref="BsonIdAttribute.AutoId"/> if set, otherwise <see langword="false"/>.</returns>
        protected virtual bool CheckIdAttribute(MemberInfo mi)
		{
            // Check if the bson id attribute is set on the given member.
            // Use the BsonIdAttribute by default.
            return ((BsonIdAttribute)CustomAttributeExtensions.GetCustomAttributes(mi, typeof(BsonIdAttribute), true).FirstOrDefault())?.AutoId ?? true;
        }

        /// <summary>
        /// Override this function to customize BsonRef attribute.
        /// </summary>
        /// <param name="mi">The member info BsonRef attribute will be checked on.</param>
        /// <param name="found">Gets whether the attribute was set on <paramref name="mi"/>.</param>
        /// <returns>Returns the value of <seealso cref="BsonRefAttribute.Collection"/> if set, otherwise <see langword="null"/>.</returns>
        protected virtual string CheckRefAttribute(MemberInfo mi, out bool found)
		{
            // Check if the bson ref attribute is set on the given member.
            // Use the BsonRefAttribute by default.

            // TODO: Consider wrapping the return value to a specific interface instead of requiring 'out'.
            BsonRefAttribute attr =((BsonRefAttribute)CustomAttributeExtensions.GetCustomAttributes(mi, typeof(BsonRefAttribute), false).FirstOrDefault());
            if (attr == null)
			{
                found = false;
                return null;
			}

            found = true;
            return attr.Collection;
        }

        /// <summary>
        /// Use this method to override how your class can be, by default, mapped from entity to Bson document.
        /// Returns an EntityMapper from each requested Type
        /// </summary>
        protected void BuildEntityMapper(EntityMapper mapper)
        {
            // var mapper = new EntityMapper(type);
            // _entities[type] = mapper;//direct add into entities, to solove the DBRef [ GetEntityMapper > BuildAddEntityMapper > RegisterDbRef > RegisterDbRefItem > GetEntityMapper ] Loop call recursion,we stoped at here and GetEntityMapper's _entities.TryGetValue

            var idAttr = typeof(BsonIdAttribute);
            var ignoreAttr = typeof(BsonIgnoreAttribute);
            var fieldAttr = typeof(BsonFieldAttribute);
            var dbrefAttr = typeof(BsonRefAttribute);

            var members = this.GetTypeMembers(mapper.ForType);
            var id = this.GetIdMember(members);

            foreach (var memberInfo in members)
            {
                // Check [BsonIgnore].
                if (CheckIgnoreAttribute(memberInfo)) continue;

                // checks field name conversion
                var name = this.ResolveFieldName(memberInfo.Name);

                // check if property has [BsonField]
                string fn = CheckFieldAttribute(memberInfo);
                
                // check if property has [BsonField] with a custom field name
                if (fn != null) name = fn;

                // checks if memberInfo is id field
                if (memberInfo == id)
                {
                    name = "_id";
                }

                // create getter/setter function
                var getter = Reflection.CreateGenericGetter(mapper.ForType, memberInfo);
                var setter = Reflection.CreateGenericSetter(mapper.ForType, memberInfo);

                // check if property has [BsonId] to get with was setted AutoId = true
                bool autoId = CheckIdAttribute(memberInfo);

                // get data type
                var dataType = memberInfo is PropertyInfo ?
                    (memberInfo as PropertyInfo).PropertyType :
                    (memberInfo as FieldInfo).FieldType;

                // check if datatype is list/array
                var isEnumerable = Reflection.IsEnumerable(dataType);

                // create a property mapper
                var member = new MemberMapper
                {
                    AutoId = autoId,
                    FieldName = name,
                    MemberName = memberInfo.Name,
                    DataType = dataType,
                    IsEnumerable = isEnumerable,
                    UnderlyingType = isEnumerable ? Reflection.GetListItemType(dataType) : dataType,
                    Getter = getter,
                    Setter = setter
                };

                // Check if property has [BsonRef].
                string dbRef = CheckRefAttribute(memberInfo, out bool dbRefFound);

                if (dbRefFound && memberInfo is PropertyInfo)
                {
                    BsonMapper.RegisterDbRef(this, member, _typeNameBinder, dbRef ?? this.ResolveCollectionName((memberInfo as PropertyInfo).PropertyType));
                }

                // support callback to user modify member mapper
                this.ResolveMember?.Invoke(mapper.ForType, memberInfo, member);

                // test if has name and there is no duplicate field
                // when member is not ignore
                if (member.FieldName != null && mapper.Members.Any(x => x.FieldName.Equals(name, StringComparison.OrdinalIgnoreCase)) == false && !member.IsIgnore)
                {
                    mapper.Members.Add(member);
                }
            }
        }
        
        /// <summary>
        /// Gets MemberInfo that refers to Id from a document object.
        /// </summary>
        protected virtual MemberInfo GetIdMember(IEnumerable<MemberInfo> members)
        {
            return Reflection.SelectMember(members,
                x => CustomAttributeExtensions.IsDefined(x, typeof(BsonIdAttribute), true),
                x => x.Name.Equals("Id", StringComparison.OrdinalIgnoreCase),
                x => x.Name.Equals(x.DeclaringType.Name + "Id", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns all member that will be have mapper between POCO class to document
        /// </summary>
        protected virtual IEnumerable<MemberInfo> GetTypeMembers(Type type)
        {
            var members = new List<MemberInfo>();

            var flags = this.IncludeNonPublic ?
                (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) :
                (BindingFlags.Public | BindingFlags.Instance);

            members.AddRange(type.GetProperties(flags)
                .Where(x => x.CanRead && x.GetIndexParameters().Length == 0)
                .Select(x => x as MemberInfo));

            if (this.IncludeFields)
            {
                members.AddRange(type.GetFields(flags).Where(x => !x.Name.EndsWith("k__BackingField") && x.IsStatic == false).Select(x => x as MemberInfo));
            }

            return members;
        }

        /// <summary>
        /// Get best construtor to use to initialize this entity.
        /// - Look if contains [BsonCtor] attribute
        /// - Look for parameterless ctor
        /// - Look for first contructor with parameter and use BsonDocument to send RawValue
        /// </summary>
        protected virtual CreateObject GetTypeCtor(EntityMapper mapper)
        {
            Type type = mapper.ForType;
            List<CreateObject> Mappings = new List<CreateObject>();
            bool returnZeroParamNull = false;
            foreach (ConstructorInfo ctor in type.GetConstructors())
            {
                ParameterInfo[] pars = ctor.GetParameters();
                // For 0 parameters, we can let the Reflection.CreateInstance handle it, unless they've specified a [BsonCtor] attribute on a different constructor.
                if (pars.Length == 0)
                {
                    returnZeroParamNull = true;
                    continue;
                }
                KeyValuePair<string, Type>[] paramMap = new KeyValuePair<string, Type>[pars.Length];
                int i;
                for (i = 0; i < pars.Length; i++)
                {
                    ParameterInfo par = pars[i];
                    MemberMapper  mi  = null;
                    foreach (MemberMapper member in mapper.Members)
                    {
                        if (member.MemberName.ToLower() == par.Name.ToLower() && member.DataType == par.ParameterType)
                        {
                            mi = member;
                            break;
                        }
                    }
                    if (mi == null) {break;}
                    paramMap[i] = new KeyValuePair<string, Type>(mi.FieldName, mi.DataType);
                }
                if (i < pars.Length) { continue;}
                CreateObject toAdd = (BsonDocument value) =>
                Activator.CreateInstance(type, paramMap.Select(x =>
                this.Deserialize(x.Value, value[x.Key])).ToArray());
                if (ctor.GetCustomAttribute<BsonCtorAttribute>() != null)
                {
                    return toAdd;
                }
                else
                {
                    Mappings.Add(toAdd);
                }
            }
            if (returnZeroParamNull)
            {
                return null;
            }
            return Mappings.FirstOrDefault();
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
