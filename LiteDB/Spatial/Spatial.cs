using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using LiteDB;
using LiteDB.Engine;

namespace LiteDB.Spatial
{
    public static class Spatial
    {
        private static readonly object _mapperLock = new();
        private static readonly Dictionary<BsonMapper, bool> _registeredMappers = new();

        public static SpatialOptions Options { get; set; } = new SpatialOptions();

        public static void EnsurePointIndex<T>(ILiteCollection<T> collection, Expression<Func<T, GeoPoint>> selector, int precisionBits = 0)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (selector == null) throw new ArgumentNullException(nameof(selector));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            if (precisionBits <= 0)
            {
                precisionBits = Options.IndexPrecisionBits;
            }

            var getter = selector.Compile();

            SpatialMapping.EnsureComputedMember(lite, "_gh", typeof(long), entity =>
            {
                var point = getter(entity);
                if (point == null)
                {
                    return null;
                }

                var normalized = point.Normalize();
                return SpatialIndexing.ComputeMorton(normalized, precisionBits);
            });

            SpatialMapping.EnsureBoundingBox(lite, entity => getter(entity)?.Normalize());

            lite.EnsureIndex("_gh", BsonExpression.Create("$._gh"));

            SpatialMetadataStore.PersistPointIndexMetadata(lite, precisionBits);
        }

        public static void EnsureShapeIndex<T>(ILiteCollection<T> collection, Expression<Func<T, GeoShape>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));

            EnsureShapeIndexInternal(collection, selector.Compile());
        }

        public static void EnsureShapeIndex<T, TShape>(ILiteCollection<T> collection, Expression<Func<T, TShape>> selector)
            where TShape : GeoShape
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));

            EnsureShapeIndexInternal(collection, selector.Compile());
        }

        private static void EnsureShapeIndexInternal<T>(ILiteCollection<T> collection, Func<T, GeoShape> getter)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (getter == null) throw new ArgumentNullException(nameof(getter));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            SpatialMapping.EnsureBoundingBox(lite, getter);
        }

        public static bool Near(GeoPoint candidate, GeoPoint center, double radiusMeters)
        {
            if (center == null) throw new ArgumentNullException(nameof(center));
            if (radiusMeters < 0d) throw new ArgumentOutOfRangeException(nameof(radiusMeters));

            if (candidate == null)
            {
                return false;
            }

            var distance = GeoMath.DistanceMeters(center, candidate, Options.Distance);
            return distance <= radiusMeters;
        }

        public static bool Within(GeoShape candidate, GeoPolygon area)
        {
            if (area == null) throw new ArgumentNullException(nameof(area));

            return candidate switch
            {
                GeoPoint point => Geometry.ContainsPoint(area, point),
                GeoPolygon polygon => Geometry.Intersects(area, polygon) && polygon.Outer.All(p => Geometry.ContainsPoint(area, p)),
                GeoLineString line => line.Points.All(p => Geometry.ContainsPoint(area, p)),
                _ => false
            };
        }

        public static bool Intersects(GeoShape candidate, GeoShape query)
        {
            if (candidate == null || query == null)
            {
                return false;
            }

            return candidate switch
            {
                GeoPoint point when query is GeoPolygon polygon => Geometry.ContainsPoint(polygon, point),
                GeoPoint point when query is GeoLineString line => Geometry.LineContainsPoint(line, point),
                GeoPoint point when query is GeoPoint other => Math.Abs(point.Lat - other.Lat) < GeoMath.EpsilonDegrees && Math.Abs(point.Lon - other.Lon) < GeoMath.EpsilonDegrees,
                GeoLineString line when query is GeoPolygon polygon => Geometry.Intersects(line, polygon),
                GeoLineString line when query is GeoLineString other => Geometry.Intersects(line, other),
                GeoLineString line when query is GeoPoint point => Geometry.LineContainsPoint(line, point),
                GeoPolygon polygon when query is GeoPolygon other => Geometry.Intersects(polygon, other),
                GeoPolygon polygon when query is GeoLineString line => Geometry.Intersects(line, polygon),
                GeoPolygon polygon when query is GeoPoint point => Geometry.ContainsPoint(polygon, point),
                _ => false
            };
        }

        public static bool Contains(GeoShape candidate, GeoPoint point)
        {
            if (point == null) throw new ArgumentNullException(nameof(point));

            if (candidate == null)
            {
                return false;
            }

            return candidate switch
            {
                GeoPolygon polygon => Geometry.ContainsPoint(polygon, point),
                GeoLineString line => Geometry.LineContainsPoint(line, point),
                GeoPoint candidatePoint => Math.Abs(candidatePoint.Lat - point.Lat) < GeoMath.EpsilonDegrees && Math.Abs(candidatePoint.Lon - point.Lon) < GeoMath.EpsilonDegrees,
                _ => false
            };
        }

        public static IEnumerable<T> Near<T>(ILiteCollection<T> collection, Func<T, GeoPoint> selector, GeoPoint center, double radiusMeters, int? limit = null)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (center == null) throw new ArgumentNullException(nameof(center));
            if (radiusMeters < 0d) throw new ArgumentOutOfRangeException(nameof(radiusMeters));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            var precisionBits = SpatialMetadataStore.GetPointIndexPrecision(lite);
            var boundingBox = GeoMath.BoundingBoxForCircle(center, radiusMeters);
            var ranges = SpatialIndexing.CoverBoundingBox(boundingBox, precisionBits, Options.MaxCoveringCells);
            var rangePredicate = SpatialQueryBuilder.BuildRangePredicate(ranges);
            var boundingPredicate = SpatialQueryBuilder.BuildBoundingBoxPredicate(boundingBox);
            var predicate = SpatialQueryBuilder.CombineSpatialPredicates(rangePredicate, boundingPredicate);

            var source = predicate != null ? lite.Find(predicate) : lite.FindAll();
            var matches = new List<(T item, double distance)>();

            foreach (var item in source)
            {
                var point = selector(item);
                if (point == null || !boundingBox.Contains(point))
                {
                    continue;
                }

                var distance = GeoMath.DistanceMeters(center, point, Options.Distance);
                if (distance <= radiusMeters)
                {
                    matches.Add((item, distance));
                }
            }

            if (Options.SortNearByDistance)
            {
                matches.Sort((x, y) => x.distance.CompareTo(y.distance));
            }

            IEnumerable<(T item, double distance)> final = matches;

            if (limit.HasValue)
            {
                final = matches.Take(limit.Value);
            }

            return final.Select(x => x.item).ToList();
        }

        public static IEnumerable<T> WithinBoundingBox<T>(ILiteCollection<T> collection, Func<T, GeoPoint> selector, double minLat, double minLon, double maxLat, double maxLon)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (selector == null) throw new ArgumentNullException(nameof(selector));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            var boundingBox = new GeoBoundingBox(minLat, minLon, maxLat, maxLon);
            var precisionBits = SpatialMetadataStore.GetPointIndexPrecision(lite);
            var ranges = SpatialIndexing.CoverBoundingBox(boundingBox, precisionBits, Options.MaxCoveringCells);
            var rangePredicate = SpatialQueryBuilder.BuildRangePredicate(ranges);
            var boundingPredicate = SpatialQueryBuilder.BuildBoundingBoxPredicate(boundingBox);
            var predicate = SpatialQueryBuilder.CombineSpatialPredicates(rangePredicate, boundingPredicate);

            var source = predicate != null ? lite.Find(predicate) : lite.FindAll();

            return source
                .Where(entity =>
                {
                    var point = selector(entity);
                    return point != null && boundingBox.Contains(point);
                })
                .ToList();
        }

        public static IEnumerable<T> Within<T>(ILiteCollection<T> collection, Func<T, GeoShape> selector, GeoPolygon area)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (area == null) throw new ArgumentNullException(nameof(area));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            var boundingBox = area.GetBoundingBox();
            var boundingPredicate = SpatialQueryBuilder.BuildBoundingBoxPredicate(boundingBox);
            var source = boundingPredicate != null ? lite.Find(boundingPredicate) : lite.FindAll();

            return source.Where(entity =>
            {
                var shape = selector(entity);
                return shape != null && Within(shape, area);
            }).ToList();
        }

        public static IEnumerable<T> Intersects<T>(ILiteCollection<T> collection, Func<T, GeoShape> selector, GeoShape query)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (query == null) throw new ArgumentNullException(nameof(query));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            var boundingBox = query.GetBoundingBox();
            var boundingPredicate = SpatialQueryBuilder.BuildBoundingBoxPredicate(boundingBox);
            var source = boundingPredicate != null ? lite.Find(boundingPredicate) : lite.FindAll();

            return source.Where(entity =>
            {
                var shape = selector(entity);
                return shape != null && Intersects(shape, query);
            }).ToList();
        }

        public static IEnumerable<T> Contains<T>(ILiteCollection<T> collection, Func<T, GeoShape> selector, GeoPoint point)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (point == null) throw new ArgumentNullException(nameof(point));

            var lite = GetLiteCollection(collection);
            var mapper = GetMapper(lite);
            EnsureMapperRegistration(mapper);

            var boundingBox = new GeoBoundingBox(point.Lat, point.Lon, point.Lat, point.Lon);
            var boundingPredicate = SpatialQueryBuilder.BuildBoundingBoxPredicate(boundingBox);
            var source = boundingPredicate != null ? lite.Find(boundingPredicate) : lite.FindAll();

            return source.Where(entity =>
            {
                var shape = selector(entity);
                return shape != null && Contains(shape, point);
            }).ToList();
        }

        private static LiteCollection<T> GetLiteCollection<T>(ILiteCollection<T> collection)
        {
            if (collection is LiteCollection<T> liteCollection)
            {
                return liteCollection;
            }

            throw new NotSupportedException("Spatial helpers require LiteCollection<T> instances.");
        }

        internal static ILiteEngine GetEngine<T>(LiteCollection<T> liteCollection)
        {
            var engineField = typeof(LiteCollection<T>).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
            if (engineField == null)
            {
                return null;
            }

            return (ILiteEngine)engineField.GetValue(liteCollection);
        }

        private static BsonMapper GetMapper<T>(LiteCollection<T> liteCollection)
        {
            var mapperField = typeof(LiteCollection<T>).GetField("_mapper", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mapperField != null)
            {
                return (BsonMapper)mapperField.GetValue(liteCollection);
            }

            return BsonMapper.Global;
        }

        private static void EnsureMapperRegistration(BsonMapper mapper)
        {
            lock (_mapperLock)
            {
                if (_registeredMappers.ContainsKey(mapper))
                {
                    return;
                }

                SpatialMapping.RegisterGeoTypes(mapper);
                _registeredMappers[mapper] = true;
            }
        }
    }

    internal static class SpatialMapping
    {
        private static readonly Type EnumerableType = typeof(System.Collections.IEnumerable);

        public static void RegisterGeoTypes(BsonMapper mapper)
        {
            mapper.RegisterType<GeoShape>(
                shape => shape == null ? BsonValue.Null : GeoJson.ToBson(shape),
                bson => DeserializeShape(bson));

            mapper.RegisterType<GeoPoint>(
                point => GeoJson.ToBson(point),
                bson => (GeoPoint)DeserializeTyped<GeoPoint>(bson));

            mapper.RegisterType<GeoLineString>(
                line => GeoJson.ToBson(line),
                bson => (GeoLineString)DeserializeTyped<GeoLineString>(bson));

            mapper.RegisterType<GeoPolygon>(
                polygon => GeoJson.ToBson(polygon),
                bson => (GeoPolygon)DeserializeTyped<GeoPolygon>(bson));
        }

        public static void EnsureBoundingBox<T>(LiteCollection<T> collection, Func<T, GeoShape> getter)
        {
            EnsureComputedMember(collection, "_mbb", typeof(double[]), entity =>
            {
                var shape = getter(entity);
                if (shape == null)
                {
                    return null;
                }

                var bbox = shape.GetBoundingBox();
                return bbox.ToArray();
            });
        }

        public static void EnsureComputedMember<T>(LiteCollection<T> collection, string fieldName, Type dataType, Func<T, object> getter)
        {
            var entity = collection.EntityMapper;
            if (entity == null)
            {
                throw new InvalidOperationException("Entity mapper not available for collection.");
            }

            entity.WaitForInitialization();

            var member = entity.Members.FirstOrDefault(x => string.Equals(x.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

            if (member == null)
            {
                var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MemberInfo memberInfo = typeof(T).GetProperty(fieldName, bindingFlags);
                if (memberInfo == null)
                {
                    memberInfo = typeof(T).GetField(fieldName, bindingFlags);
                }

                GenericSetter setter = null;
                if (memberInfo != null)
                {
                    setter = Reflection.CreateGenericSetter(typeof(T), memberInfo);
                }

                member = new MemberMapper
                {
                    FieldName = fieldName,
                    MemberName = memberInfo?.Name ?? fieldName,
                    DataType = dataType,
                    UnderlyingType = dataType,
                    IsEnumerable = EnumerableType.IsAssignableFrom(dataType) && dataType != typeof(string),
                    Getter = obj => getter((T)obj),
                    Setter = setter
                };

                entity.Members.Add(member);
            }
            else
            {
                member.Getter = obj => getter((T)obj);
                member.DataType = dataType;
                member.UnderlyingType = dataType;
                member.IsEnumerable = EnumerableType.IsAssignableFrom(dataType) && dataType != typeof(string);
            }
        }

        private static GeoShape DeserializeShape(BsonValue bson)
        {
            if (bson == null || bson.IsNull)
            {
                return null;
            }

            if (!bson.IsDocument)
            {
                throw new LiteException(0, "GeoJSON value must be a document.");
            }

            return GeoJson.FromBson(bson.AsDocument);
        }

        private static GeoShape DeserializeTyped<TShape>(BsonValue bson) where TShape : GeoShape
        {
            var shape = DeserializeShape(bson);
            if (shape == null)
            {
                return null;
            }

            if (shape is TShape typed)
            {
                return typed;
            }

            throw new LiteException(0, $"GeoJSON payload describes a '{shape.GetType().Name}', not '{typeof(TShape).Name}'.");
        }
    }
}
