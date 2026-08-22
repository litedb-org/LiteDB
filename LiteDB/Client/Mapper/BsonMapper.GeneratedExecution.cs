using System;
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

        internal EntityMapper GetGeneratedEntityMapper(Type type)
        {
            if (_generatedEntities.TryGetValue(type, out var entityMapper))
            {
                return entityMapper;
            }

            throw new InvalidOperationException($"No source-generated entity mapper is registered for '{type.FullName}'.");
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
                ResolveFieldName != ResolveFieldNameDefault ||
                ResolveMember != ResolveMemberDefault)
            {
                throw new InvalidOperationException(
                    "The registered source-generated execution map does not support the active BsonMapper configuration. " +
                    "Use GetCollection<T> for customized mapper behavior until the generated execution contract supports that configuration.");
            }

            return new GeneratedExecutionOptions(
                SerializeNullValues,
                TrimWhitespace,
                EmptyStringToNull,
                EnumAsInteger);
        }
    }
}
