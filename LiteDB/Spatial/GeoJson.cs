using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace LiteDB.Spatial
{
    public static class GeoJson
    {
        public static string Serialize(GeoShape shape)
        {
            if (shape == null)
            {
                throw new ArgumentNullException(nameof(shape));
            }

            var bson = ToBson(shape);
            return JsonSerializer.Serialize(bson, indent: false);
        }

        public static T Deserialize<T>(string json)
            where T : GeoShape
        {
            var shape = Deserialize(json);

            if (shape is T typed)
            {
                return typed;
            }

            throw new LiteException(0, $"GeoJSON payload describes a '{shape?.GetType().Name}', not '{typeof(T).Name}'.");
        }

        public static GeoShape Deserialize(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var bson = JsonSerializer.Deserialize(json);
            if (!bson.IsDocument)
            {
                throw new LiteException(0, "GeoJSON payload must be a JSON object");
            }

            var document = bson.AsDocument;
            if (document.ContainsKey("crs"))
            {
                throw new LiteException(0, "Only the default WGS84 CRS is supported.");
            }

            return FromBson(document);
        }

        internal static BsonValue ToBson(GeoShape shape)
        {
            return shape switch
            {
                null => BsonValue.Null,
                GeoPoint point => new BsonDocument
                {
                    ["type"] = "Point",
                    ["coordinates"] = new BsonArray { point.Lon, point.Lat }
                },
                GeoLineString line => new BsonDocument
                {
                    ["type"] = "LineString",
                    ["coordinates"] = new BsonArray(line.Points.Select(p => new BsonArray { p.Lon, p.Lat }))
                },
                GeoPolygon polygon => new BsonDocument
                {
                    ["type"] = "Polygon",
                    ["coordinates"] = BuildPolygonCoordinates(polygon)
                },
                _ => throw new LiteException(0, $"Unsupported GeoShape type '{shape.GetType().Name}'.")
            };
        }

        internal static GeoShape FromBson(BsonDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (!document.TryGetValue("type", out var typeValue) || !typeValue.IsString)
            {
                throw new LiteException(0, "GeoJSON requires a string 'type' property");
            }

            var type = typeValue.AsString;

            return type switch
            {
                "Point" => ParsePoint(document),
                "LineString" => ParseLineString(document),
                "Polygon" => ParsePolygon(document),
                _ => throw new LiteException(0, $"Unsupported GeoJSON geometry type '{type}'")
            };
        }

        private static BsonArray BuildPolygonCoordinates(GeoPolygon polygon)
        {
            var rings = new List<BsonArray>
            {
                new BsonArray(polygon.Outer.Select(p => new BsonArray { p.Lon, p.Lat }))
            };

            foreach (var hole in polygon.Holes)
            {
                rings.Add(new BsonArray(hole.Select(p => new BsonArray { p.Lon, p.Lat })));
            }

            return new BsonArray(rings);
        }

        private static GeoPoint ParsePoint(BsonDocument document)
        {
            var (lon, lat) = ReadCoordinateArray(document);
            return new GeoPoint(lat, lon);
        }

        private static GeoLineString ParseLineString(BsonDocument document)
        {
            var array = EnsureArray(document, "coordinates");
            var points = array.Select(ToPoint).ToList();
            return new GeoLineString(points);
        }

        private static GeoPolygon ParsePolygon(BsonDocument document)
        {
            var array = EnsureArray(document, "coordinates");

            if (array.Count == 0)
            {
                throw new LiteException(0, "Polygon must contain at least one ring");
            }

            var rings = array.Select(ToRing).ToList();
            var outer = rings[0];
            var holes = rings.Skip(1).Select(r => (IReadOnlyList<GeoPoint>)r).ToList();
            return new GeoPolygon(outer, holes);
        }

        private static (double lon, double lat) ReadCoordinateArray(BsonDocument document)
        {
            var array = EnsureArray(document, "coordinates");

            if (array.Count < 2)
            {
                throw new LiteException(0, "Coordinate array must contain longitude and latitude");
            }

            return (array[0].AsDouble, array[1].AsDouble);
        }

        private static List<GeoPoint> ToRing(BsonValue value)
        {
            if (!value.IsArray)
            {
                throw new LiteException(0, "Ring must be an array of positions");
            }

            return value.AsArray.Select(ToPoint).ToList();
        }

        private static GeoPoint ToPoint(BsonValue value)
        {
            if (!value.IsArray)
            {
                throw new LiteException(0, "Point must be expressed as an array");
            }

            var coords = value.AsArray;
            if (coords.Count < 2)
            {
                throw new LiteException(0, "Point array must contain longitude and latitude");
            }

            return new GeoPoint(coords[1].AsDouble, coords[0].AsDouble);
        }

        private static BsonArray EnsureArray(BsonDocument document, string key)
        {
            if (!document.TryGetValue(key, out var value) || !value.IsArray)
            {
                throw new LiteException(0, $"GeoJSON requires '{key}' to be an array");
            }

            return value.AsArray;
        }
    }
}
