using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LiteDB.Engine;
using LiteDB;

namespace LiteDB.Spatial
{
    internal static class SpatialMetadataStore
    {
        private const string MetadataCollection = "_spatial_meta";

        private static readonly ConcurrentDictionary<EngineCollectionKey, SpatialIndexMetadata> _cache = new();

        public static void PersistPointIndexMetadata<T>(LiteCollection<T> collection, int precisionBits)
        {
            var engine = Spatial.GetEngine(collection);
            if (engine == null)
            {
                return;
            }

            var key = EngineCollectionKey.Create(engine, collection.Name);

            var document = new BsonDocument
            {
                ["_id"] = new BsonValue($"{collection.Name}._gh"),
                ["collection"] = collection.Name,
                ["index"] = "_gh",
                ["precisionBits"] = precisionBits,
                ["updatedUtc"] = DateTime.UtcNow
            };

            engine.Upsert(MetadataCollection, new[] { document }, BsonAutoId.ObjectId);

            _cache[key] = new SpatialIndexMetadata(precisionBits);
        }

        public static int GetPointIndexPrecision<T>(LiteCollection<T> collection)
        {
            var engine = Spatial.GetEngine(collection);
            if (engine == null)
            {
                return Spatial.Options.DefaultIndexPrecisionBits;
            }

            var key = EngineCollectionKey.Create(engine, collection.Name);

            if (_cache.TryGetValue(key, out var metadata))
            {
                return metadata.PrecisionBits;
            }

            var predicate = Query.And(
                Query.EQ("collection", new BsonValue(collection.Name)),
                Query.EQ("index", new BsonValue("_gh")));

            try
            {
                using var reader = engine.Query(MetadataCollection, new Query { Where = { predicate } });

                if (reader.Read())
                {
                    var document = reader.Current.AsDocument;

                    if (document.TryGetValue("precisionBits", out var precisionValue) && precisionValue.IsInt32)
                    {
                        var precisionBits = precisionValue.AsInt32;
                        _cache[key] = new SpatialIndexMetadata(precisionBits);
                        return precisionBits;
                    }
                }
            }
            catch (LiteException)
            {
                // Metadata collection may not exist yet; fall back to options.
            }

            return Spatial.Options.DefaultIndexPrecisionBits;
        }

        private readonly struct SpatialIndexMetadata
        {
            public SpatialIndexMetadata(int precisionBits)
            {
                this.PrecisionBits = precisionBits;
            }

            public int PrecisionBits { get; }
        }

        private readonly struct EngineCollectionKey : IEquatable<EngineCollectionKey>
        {
            private EngineCollectionKey(ILiteEngine engine, string collection)
            {
                _engine = engine;
                _collection = collection;
            }

            private readonly ILiteEngine _engine;
            private readonly string _collection;

            public static EngineCollectionKey Create(ILiteEngine engine, string collection)
            {
                return new EngineCollectionKey(engine, collection);
            }

            public bool Equals(EngineCollectionKey other)
            {
                return ReferenceEquals(_engine, other._engine) && string.Equals(_collection, other._collection, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj)
            {
                return obj is EngineCollectionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = RuntimeHelpers.GetHashCode(_engine);
                    hash = (hash * 397) ^ (_collection == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(_collection));
                    return hash;
                }
            }
        }
    }
}
