using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private readonly ConcurrentDictionary<Type, object> _generatedExecutionMaps = new ConcurrentDictionary<Type, object>();
        private readonly bool _hasCustomTypeInstantiator;
        private readonly bool _hasCustomTypeNameBinder;
        private int _customTypeRegistrations;
        private int _customEntityConfigurations;
        private bool _registeringBuiltInTypes;

        private static string ResolveFieldNameDefault(string name) => name;

        private static void ResolveMemberDefault(Type _, MemberInfo __, MemberMapper ___)
        {
        }

        /// <summary>
        /// Registers a statically authored execution map for an entity that already has a source-generated entity mapper.
        /// </summary>
        /// <typeparam name="T">The exact entity type handled by <paramref name="map"/>.</typeparam>
        /// <param name="map">The immutable map that converts between <typeparamref name="T"/> and BSON documents.</param>
        /// <returns>The registered map.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="map"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the entity mapper or execution map registration is invalid.</exception>
        public GeneratedEntityMap<T> RegisterGeneratedExecutionMap<T>(GeneratedEntityMap<T> map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));

            var type = typeof(T);
            if (HasGeneratedEntityMapper(type) == false)
            {
                throw new InvalidOperationException($"A source-generated entity mapper must be registered before registering an execution map for '{type.FullName}'.");
            }

            if (_generatedExecutionMaps.TryAdd(type, map) == false)
            {
                throw new InvalidOperationException($"A source-generated execution map is already registered for '{type.FullName}'.");
            }

            return map;
        }

        internal bool TryGetGeneratedExecutionMap<T>(out GeneratedEntityMap<T> map)
        {
            if (_generatedExecutionMaps.TryGetValue(typeof(T), out var registeredMap) && registeredMap is GeneratedEntityMap<T> typedMap)
            {
                map = typedMap;
                return true;
            }

            map = null;
            return false;
        }

        private void RecordCustomTypeRegistration()
        {
            if (_registeringBuiltInTypes == false)
            {
                Interlocked.Increment(ref _customTypeRegistrations);
            }
        }

        internal void RecordCustomEntityConfiguration()
        {
            Interlocked.Increment(ref _customEntityConfigurations);
        }

        internal EntityMapper GetGeneratedEntityMapper(Type type)
        {
            if (_generatedEntities.TryGetValue(type, out var entityMapper))
            {
                return entityMapper;
            }

            throw new InvalidOperationException($"No source-generated entity mapper is registered for '{type.FullName}'.");
        }

        /// <summary>
        /// Serializes a value captured by a LINQ expression on a generated or BsonDocument collection without
        /// runtime member discovery. Produces what <see cref="Serialize(Type, object, int)"/> produces when it is given
        /// the value's runtime type, which is how the runtime-mapping visitor serializes constants as well, and
        /// rejects anything that the ordinary mapper would hand to reflection-based object mapping.
        /// </summary>
        internal BsonValue SerializeGeneratedConstant(object value)
        {
            return this.SerializeGeneratedConstant(value, 0);
        }

        private BsonValue SerializeGeneratedConstant(object value, int depth)
        {
            if (++depth > MaxDepth) throw LiteException.DocumentMaxDepth(MaxDepth, value?.GetType());

            switch (value)
            {
                case null: return BsonValue.Null;
                case BsonValue bson: return bson;
                case string text:
                    var trimmed = TrimWhitespace ? text.Trim() : text;
                    return EmptyStringToNull && trimmed.Length == 0 ? BsonValue.Null : new BsonValue(trimmed);
                case int number: return new BsonValue(number);
                case long number: return new BsonValue(number);
                case double number: return new BsonValue(number);
                case decimal number: return new BsonValue(number);
                case bool flag: return new BsonValue(flag);
                case DateTime date: return new BsonValue(date);
                case Guid guid: return new BsonValue(guid);
                case ObjectId objectId: return new BsonValue(objectId);
                case byte[] bytes when value.GetType() == typeof(byte[]): return new BsonValue(bytes);
                case short or ushort or byte or sbyte: return new BsonValue(Convert.ToInt32(value));
                case uint number: return new BsonValue((long)number);
                case ulong number: return new BsonValue(unchecked((long)number));
                case float number: return new BsonValue((double)number);
                case char character: return new BsonValue(character.ToString());
                case Enum enumeration:
                    return EnumAsInteger ? new BsonValue(Convert.ToInt32(enumeration)) : new BsonValue(enumeration.ToString());
            }

            var type = value.GetType();

            // Registered converters (built in: Uri, DateTimeOffset, TimeSpan, Regex) are plain delegates, so they
            // are safe to call. A generated collection rejects a mapper with user registrations before it gets
            // here; a BsonDocument collection may use them.
            if (_customSerializer.TryGetValue(type, out var custom))
            {
                return custom(value);
            }

            if (_generatedExecutionMaps.TryGetValue(type, out var map))
            {
                var options = new GeneratedExecutionOptions(SerializeNullValues, TrimWhitespace, EmptyStringToNull, EnumAsInteger);

                return ((IGeneratedEntityMap)map).Serialize(value, options);
            }

            if (value is IEnumerable items && value is not IDictionary)
            {
                var array = new BsonArray();

                foreach (var item in items)
                {
                    array.Add(this.SerializeGeneratedConstant(item, depth));
                }

                return array;
            }

            throw new NotSupportedException(
                $"A LINQ expression captured a value of type '{type.FullName}'. Source-generated and BsonDocument " +
                "collections never map application types at runtime: capture a BSON-native value, an enum, a type with a " +
                "registered converter, a collection of those, or an instance of a [BsonSourceGenerated] type.");
        }

        internal GeneratedExecutionOptions ValidateGeneratedExecutionConfiguration<T>(GeneratedEntityMap<T> map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));

            if (((SerializeNullValues ||
                    TrimWhitespace == false ||
                    EmptyStringToNull == false ||
                    EnumAsInteger) && map.SupportsScalarOptions == false) ||
                MaxDepth != 20 ||
                IncludeFields ||
                IncludeNonPublic ||
                OnDeserialization is not null ||
                _hasCustomTypeInstantiator ||
                _hasCustomTypeNameBinder ||
                Volatile.Read(ref _customTypeRegistrations) != 0 ||
                Volatile.Read(ref _customEntityConfigurations) != 0 ||
                ResolveFieldName != ResolveFieldNameDefault ||
                ResolveMember != ResolveMemberDefault ||
                typeof(T).IsSealed == false ||
                GetGeneratedEntityMapper(typeof(T)).Members.Exists(member => member.IsDbRef))
            {
                var detail = typeof(T).IsSealed
                    ? string.Empty
                    : $" Entity type '{typeof(T).FullName}' must be sealed because generated collections do not support derived runtime types.";

                throw new InvalidOperationException(
                    "The registered source-generated execution map does not support the active BsonMapper configuration. " +
                    "Use GetCollection<T> for customized mapper behavior until the generated execution contract supports that configuration." +
                    detail);
            }

            return new GeneratedExecutionOptions(
                SerializeNullValues,
                TrimWhitespace,
                EmptyStringToNull,
                EnumAsInteger);
        }
    }
}
