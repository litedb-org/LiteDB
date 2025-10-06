using System;
using LiteDB.Spatial;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        public static BsonValue SPATIAL_INTERSECTS_MBB(BsonValue mbb, BsonValue minLat, BsonValue minLon, BsonValue maxLat, BsonValue maxLon)
        {
            if (mbb == null || mbb.IsNull || !mbb.IsArray || mbb.AsArray.Count != 4)
            {
                return false;
            }

            if (!minLat.IsNumber || !minLon.IsNumber || !maxLat.IsNumber || !maxLon.IsNumber)
            {
                return false;
            }

            var candidate = ToBoundingBox(mbb);
            var query = new GeoBoundingBox(minLat.AsDouble, minLon.AsDouble, maxLat.AsDouble, maxLon.AsDouble);

            return candidate.Intersects(query);
        }

        public static BsonValue SPATIAL_MBB_INTERSECTS(BsonValue mbb, BsonValue minLat, BsonValue minLon, BsonValue maxLat, BsonValue maxLon)
        {
            return SPATIAL_INTERSECTS_MBB(mbb, minLat, minLon, maxLat, maxLon);
        }

        public static BsonValue SPATIAL_NEAR(BsonValue candidate, BsonValue center, BsonValue radius)
        {
            return SPATIAL_NEAR(candidate, center, radius, BsonValue.Null);
        }

        public static BsonValue SPATIAL_NEAR(BsonValue candidate, BsonValue center, BsonValue radius, BsonValue formula)
        {
            if (!radius.IsNumber)
            {
                return false;
            }

            var candidatePoint = ToShape(candidate) as GeoPoint;
            var centerPoint = ToShape(center) as GeoPoint;

            if (candidatePoint == null || centerPoint == null)
            {
                return false;
            }

            var distanceFormula = ParseFormula(formula);
            var radiusMeters = radius.AsDouble;

            return SpatialExpressions.Near(candidatePoint, centerPoint, radiusMeters, distanceFormula);
        }

        public static BsonValue SPATIAL_WITHIN_BOX(BsonValue value, BsonValue minLat, BsonValue minLon, BsonValue maxLat, BsonValue maxLon)
        {
            var shape = ToShape(value);
            if (shape == null)
            {
                return false;
            }

            var box = new GeoBoundingBox(minLat.AsDouble, minLon.AsDouble, maxLat.AsDouble, maxLon.AsDouble);

            return shape switch
            {
                GeoPoint point => SpatialExpressions.WithinBoundingBox(point, box.MinLat, box.MinLon, box.MaxLat, box.MaxLon),
                GeoShape geoShape => geoShape.GetBoundingBox().Intersects(box),
                _ => false
            };
        }

        public static BsonValue SPATIAL_WITHIN(BsonValue candidate, BsonValue polygon)
        {
            var shape = ToShape(candidate);
            var area = ToShape(polygon) as GeoPolygon;

            if (shape == null || area == null)
            {
                return false;
            }

            return SpatialExpressions.Within(shape, area);
        }

        public static BsonValue SPATIAL_INTERSECTS(BsonValue candidate, BsonValue other)
        {
            var left = ToShape(candidate);
            var right = ToShape(other);

            if (left == null || right == null)
            {
                return false;
            }

            return SpatialExpressions.Intersects(left, right);
        }

        public static BsonValue SPATIAL_CONTAINS(BsonValue candidate, BsonValue point)
        {
            var shape = ToShape(candidate);
            var geoPoint = ToShape(point) as GeoPoint;

            if (shape == null || geoPoint == null)
            {
                return false;
            }

            return SpatialExpressions.Contains(shape, geoPoint);
        }

        public static BsonValue SPATIAL_CONTAINS_POINT(BsonValue candidate, BsonValue point)
        {
            return SPATIAL_CONTAINS(candidate, point);
        }

        private static GeoBoundingBox ToBoundingBox(BsonValue value)
        {
            var array = value.AsArray;
            return new GeoBoundingBox(array[0].AsDouble, array[1].AsDouble, array[2].AsDouble, array[3].AsDouble);
        }

        private static GeoShape ToShape(BsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return null;
            }

            if (value.IsDocument)
            {
                return GeoJson.FromBson(value.AsDocument);
            }

            if (value.IsArray && value.AsArray.Count >= 2)
            {
                var array = value.AsArray;
                var lon = array[0].AsDouble;
                var lat = array[1].AsDouble;
                return new GeoPoint(lat, lon);
            }

            if (value.RawValue is GeoShape shape)
            {
                return shape;
            }

            return null;
        }

        private static DistanceFormula ParseFormula(BsonValue formula)
        {
            if (formula == null || formula.IsNull)
            {
                return Spatial.Spatial.Options.Distance;
            }

            if (formula.IsString && Enum.TryParse(formula.AsString, out DistanceFormula parsed))
            {
                return parsed;
            }

            if (formula.IsInt32)
            {
                return (DistanceFormula)formula.AsInt32;
            }

            return Spatial.Spatial.Options.Distance;
        }
    }
}
