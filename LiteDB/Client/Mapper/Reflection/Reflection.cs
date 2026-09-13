using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    #region Delegates

    public delegate object CreateObject(BsonDocument value);

    public delegate void GenericSetter(object target, object value);

    public delegate object GenericGetter(object obj);

    #endregion

    /// <summary>
    /// Helper class to get entity properties and map as BsonValue
    /// </summary>
    internal partial class Reflection
    {
        #region CreateInstance

        private static readonly Dictionary<Type, CreateObject> _cacheCtor = new Dictionary<Type, CreateObject>();

        /// <summary>
        /// Create a new instance from a Type
        /// </summary>
        public static object CreateInstance(Type type)
        {
            try
            {
                if (_cacheCtor.TryGetValue(type, out CreateObject c))
                {
                    return c(null);
                }
            }
            catch (Exception ex)
            {
                throw LiteException.InvalidCtor(type, ex);
            }

            lock (_cacheCtor)
            {
                try
                {
                    if (_cacheCtor.TryGetValue(type, out CreateObject c))
                    {
                        return c(null);
                    }

                    var typeInfo = type.GetTypeInfo();

                    if (typeInfo.IsClass)
                    {
                        _cacheCtor.Add(type, c = CreateClass(type));
                    }
                    else if (typeInfo.IsInterface) // some know interfaces
                    {
                        if (typeInfo.IsGenericType)
                        {
                            var typeDef = type.GetGenericTypeDefinition();

                            if (typeDef == typeof(ISet<>))
                            {
                                return CreateInstance(GetGenericSetOfType(UnderlyingTypeOf(type)));
                            }
                            else if (typeDef == typeof(IDictionary<,>))
                            {
                                var k = type.GetGenericArguments()[0];
                                var v = type.GetGenericArguments()[1];

                                return CreateInstance(GetGenericDictionaryOfType(k, v));
                            }
                            else if (typeDef == typeof(IList<>) ||
                                     typeDef == typeof(ICollection<>) ||
                                     typeDef == typeof(IEnumerable<>) ||
                                     typeof(IEnumerable).IsAssignableFrom(typeDef))
                            {
                                return CreateInstance(GetGenericListOfType(UnderlyingTypeOf(type)));
                            }
                        }

                        throw LiteException.InvalidCtor(type, null);
                    }
                    else // structs
                    {
                        _cacheCtor.Add(type, c = CreateStruct(type));
                    }

                    return c(null);
                }
                catch (Exception ex)
                {
                    throw LiteException.InvalidCtor(type, ex);
                }
            }
        }

        #endregion

        #region Utils

        /// <summary>
        /// Get a list from all acepted data type to property converter BsonValue
        /// </summary>
        public static readonly Dictionary<Type, PropertyInfo> ConvertType = new Dictionary<Type, PropertyInfo>()
        {
            [typeof(DateTime)] = typeof(BsonValue).GetProperty("AsDateTime"),
            [typeof(decimal)] = typeof(BsonValue).GetProperty("AsDecimal"),
            [typeof(double)] = typeof(BsonValue).GetProperty("AsDouble"),
            [typeof(long)] = typeof(BsonValue).GetProperty("AsInt64"),
            [typeof(int)] = typeof(BsonValue).GetProperty("AsInt32"),
            [typeof(bool)] = typeof(BsonValue).GetProperty("AsBoolean"),
            [typeof(byte[])] = typeof(BsonValue).GetProperty("AsBinary"),
            [typeof(BsonDocument)] = typeof(BsonValue).GetProperty("AsDocument"),
            [typeof(BsonArray)] = typeof(BsonValue).GetProperty("AsArray"),
            [typeof(ObjectId)] = typeof(BsonValue).GetProperty("AsObjectId"),
            [typeof(string)] = typeof(BsonValue).GetProperty("AsString"),
            [typeof(Guid)] = typeof(BsonValue).GetProperty("AsGuid")
        };

        // getting `bsonDocument.Item[string]` property access
        public static readonly PropertyInfo DocumentItemProperty =
            typeof(BsonDocument).GetProperties()
            .Where(x => x.Name == "Item" && x.GetGetMethod().GetParameters().First().ParameterType == typeof(String))
            .First();

        public static bool IsNullable(Type type)
        {
            if (!type.GetTypeInfo().IsGenericType) return false;
            var g = type.GetGenericTypeDefinition();
            return (g.Equals(typeof(Nullable<>)));
        }

        /// <summary>
        /// Get underlying get - using to get inner Type from Nullable type
        /// </summary>
        public static Type UnderlyingTypeOf(Type type)
        {
            if (!type.GetTypeInfo().IsGenericType) return type;

            return type.GetGenericArguments()[0];
        }

        public static Type GetGenericListOfType(Type type)
        {
            var listType = typeof(List<>);
            return listType.MakeGenericType(type);
        }

        public static Type GetGenericSetOfType(Type type)
        {
            var setType = typeof(HashSet<>);
            return setType.MakeGenericType(type);
        }

        public static Type GetGenericDictionaryOfType(Type k, Type v)
        {
            var dictionaryType = typeof(Dictionary<,>);
            return dictionaryType.MakeGenericType(k, v);
        }

        /// <summary>
        /// Get item type from a generic List or Array
        /// </summary>
        public static Type GetListItemType(Type listType)
        {
            if (listType.IsArray) return listType.GetElementType();

            foreach (var i in listType.GetInterfaces())
            {
                if (i.GetTypeInfo().IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return i.GetGenericArguments()[0];
                }
                // if interface is IEnumerable (non-generic), let's get from listType and not from interface
                // from #395
                else if (listType.GetTypeInfo().IsGenericType && i == typeof(IEnumerable))
                {
                    return listType.GetGenericArguments()[0];
                }
            }

            return typeof(object);
        }

        /// <summary>
        /// Returns true if Type is any kind of Array/IList/ICollection/....
        /// </summary>
        public static bool IsEnumerable(Type type)
        {
            if (type == typeof(IEnumerable) || type.IsArray) return true;
            if (type == typeof(string)) return false; // do not define "String" as IEnumerable<char>

            foreach (var @interface in type.GetInterfaces())
            {
                if (@interface.GetTypeInfo().IsGenericType)
                {
                    if (@interface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    {
                        // if needed, you can also return the type used as generic argument
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Return if type is simple value
        /// </summary>
        public static bool IsSimpleType(Type type)
        {
            return
                type == typeof(string) ||
                type == typeof(Boolean) ||
                type == typeof(Byte) ||
                type == typeof(SByte) ||
                type == typeof(Int16) ||
                type == typeof(Int32) ||
                type == typeof(Int64) ||
                type == typeof(UInt16) ||
                type == typeof(UInt32) ||
                type == typeof(UInt64) ||
                type == typeof(Double) ||
                type == typeof(Single) ||
                type == typeof(Decimal) ||
                type == typeof(ObjectId) ||
                type == typeof(DateTime) ||
                type == typeof(Guid);
        }

        /// <summary>
        /// Returns true if Type implement ICollection (like List, HashSet)
        /// </summary>
        public static bool IsCollection(Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition().Equals(typeof(ICollection<>)) ||
                type.GetInterfaces().Any(x => x == typeof(ICollection) ||
                (x.GetTypeInfo().IsGenericType ? x.GetGenericTypeDefinition() == typeof(ICollection<>) : false));
        }

        /// <summary>
        /// Returns if Type is a generic Dictionary
        /// </summary>
        public static bool IsDictionary(Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition().Equals(typeof(IDictionary<,>)) ||
                type.GetInterfaces().Any(x => x == typeof(IDictionary) ||
                (x.GetTypeInfo().IsGenericType ? x.GetGenericTypeDefinition().Equals(typeof(IDictionary<,>)) : false));
        }

        /// <summary>
        /// Select member from a list of member using predicate order function to select
        /// </summary>
        public static MemberInfo SelectMember(IEnumerable<MemberInfo> members, params Func<MemberInfo, bool>[] predicates)
        {
            foreach (var predicate in predicates)
            {
                var member = members.FirstOrDefault(predicate);

                if (member != null)
                {
                    return member;
                }
            }

            return null;
        }

        #endregion

        #region MethodName

        private static readonly Dictionary<MethodInfo, string> _cacheName = new Dictionary<MethodInfo, string>();

        /// <summary>
        /// Get a friendly method name with parameter types
        /// </summary>
        public static string MethodName(MethodInfo method, int skipParameters = 0)
        {
            lock (_cacheName)
            {
                if (_cacheName.TryGetValue(method, out var value))
                {
                    return value;
                }

                value = MethodNameInternal(method, skipParameters);

                _cacheName.Add(method, value);


                return value;
            }
        }

        private static string MethodNameInternal(MethodInfo method, int skipParameters = 0)
        {
            var sb = new StringBuilder(method.Name + "(");
            var index = 0;

            foreach (var p in method.GetParameters().Skip(skipParameters))
            {
                if (index++ > 0) sb.Append(",");

                sb.Append(FriendlyTypeName(p.ParameterType));

                if (p.ParameterType.GetTypeInfo().IsGenericType)
                {
                    var generic = p.ParameterType.GetGenericTypeDefinition();

                    var types = generic.GetGenericArguments();

                    sb.Append("<");

                    sb.Append(string.Join(",", types.Select(x => FriendlyTypeName(x))));

                    sb.Append(">");
                }
            }

            sb.Append(")");
            return sb.ToString();
        }

        /// <summary>
        /// Get C# friendly primitive type names
        /// </summary>
        private static string FriendlyTypeName(Type type)
        {
            var generic = type.Name.IndexOf("`");

            switch (type.FullName)
            {
                case "System.Object": return "object";
                case "System.String": return "string";
                case "System.Boolean": return "bool";
                case "System.Byte": return "byte";
                case "System.Char": return "char";
                case "System.Decimal": return "decimal";
                case "System.Double": return "double";
                case "System.Int16": return "short";
                case "System.Int32": return "int";
                case "System.Int64": return "long";
                case "System.SByte": return "sbyte";
                case "System.Single": return "float";
                case "System.UInt16": return "ushort";
                case "System.UInt32": return "uint";
                case "System.UInt64": return "ulong";

                default: return generic > 0 ? type.Name.Substring(0, generic) : type.Name;
            }
        }

        #endregion
    }
}