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

        /// <summary>
        /// Registers a mapping for one of LiteDB's own models. Unlike the public registration methods this is
        /// idempotent, because every storage instance on the same mapper asks for it.
        /// </summary>
        internal void TryRegisterGeneratedMapping<T>(EntityMapper entity, GeneratedEntityMap<T> map)
        {
            _generatedEntities.TryAdd(typeof(T), entity);
            _generatedExecutionMaps.TryAdd(typeof(T), map);
        }

        /// <summary>
        /// Converts a file storage id without runtime model mapping.
        /// </summary>
        private const string FileIdJustification =
            "Custom ID mapping is entered only through GetStorage<TFileId> or the LiteStorage<TFileId> constructor, " +
            "both of which surface RequiresUnreferencedCode and RequiresDynamicCode to callers. The unannotated " +
            "FileStorage property uses string IDs, which never enter runtime mapping. FileIdMembers preserves " +
            "the top-level custom ID only, not nested member types; callers must heed the public API warnings.";

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = FileIdJustification)]
        internal BsonValue SerializeFileId<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(AotCompatibility.FileIdMembers)] T>(T id)
        {
            if (id == null) return BsonValue.Null;
            if (id is BsonValue bson) return bson;
            // Match Serialize's converter precedence, including converters for BSON-native ID types.
            if (_customSerializer.TryGetValue(typeof(T), out var custom) || _customSerializer.TryGetValue(id.GetType(), out custom))
            {
                return custom(id);
            }

            return IsMappingFreeFileId(typeof(T)) ? this.SerializeGeneratedConstant(id) : this.Serialize(typeof(T), id);
        }

        /// <summary>
        /// BSON-native types, enums and types with a registered converter need no model mapping.
        /// </summary>
        private bool IsMappingFreeFileId(Type type)
        {
            return GeneratedScalarConverter.CanConvert(type) || _customSerializer.ContainsKey(type);
        }

        /// <summary>
        /// Converts a stored file storage id back without runtime model mapping.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = FileIdJustification)]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = FileIdJustification)]
        internal T DeserializeFileId<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(AotCompatibility.FileIdMembers)] T>(BsonValue value)
        {
            if (_customDeserializer.TryGetValue(typeof(T), out var custom)) return (T)custom(value);
            if (GeneratedScalarConverter.CanConvert(typeof(T))) return GeneratedScalarConverter.Convert<T>(value);

            return (T)this.Deserialize(typeof(T), value);
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

        internal bool TrySerializeRegistered<T>(T value, out BsonValue serialized)
        {
            if (_customSerializer.TryGetValue(typeof(T), out var custom))
            {
                serialized = custom(value);
                return true;
            }

            serialized = null;
            return false;
        }

        internal bool TryDeserializeRegistered<T>(BsonValue value, out T deserialized)
        {
            if (_customDeserializer.TryGetValue(typeof(T), out var custom))
            {
                deserialized = (T)custom(value);
                return true;
            }

            deserialized = default;
            return false;
        }

        internal bool TryGetRuntimeEntityMapper(Type type, out EntityMapper entity)
        {
            if (_entities.TryGetValue(type, out entity))
            {
                entity.WaitForInitialization();
                return true;
            }

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

            if (value is IDictionary dictionary)
            {
                // The ordinary mapper converts keys through TypeDescriptor, which is not trimming safe. String
                // keys need no conversion and are what a BSON document has anyway.
                var document = new BsonDocument();

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                    {
                        throw new NotSupportedException(
                            $"A LINQ expression captured a dictionary with keys of type '{entry.Key.GetType().FullName}'. " +
                            "Source-generated and BsonDocument collections convert dictionaries with string keys only.");
                    }

                    document[key] = this.SerializeGeneratedConstant(entry.Value, depth);
                }

                return document;
            }

            if (value is IEnumerable items)
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

            if (map.IsConfigurationIndependent)
            {
                return new GeneratedExecutionOptions(SerializeNullValues, TrimWhitespace, EmptyStringToNull, EnumAsInteger);
            }

            if (MaxDepth != 20 ||
                IncludeFields ||
                IncludeNonPublic ||
                OnDeserialization is not null ||
                _hasCustomTypeInstantiator ||
                _hasCustomTypeNameBinder ||
                Volatile.Read(ref _customTypeRegistrations) != 0 ||
                Volatile.Read(ref _customEntityConfigurations) != 0 ||
                ResolveFieldName != ResolveFieldNameDefault ||
                ResolveMember != ResolveMemberDefault ||
                typeof(T).IsSealed == false)
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
