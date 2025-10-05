using LiteDB.Spatial;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        public static BsonValue SPATIAL_INTERSECTS_MBB(BsonValue mbb, BsonValue minLat, BsonValue minLon, BsonValue maxLat, BsonValue maxLon)
        {
            if (mbb == null || mbb.IsNull || !mbb.IsArray || mbb.AsArray.Count < 4)
            {
                return false;
            }

            if (!minLat.IsNumber || !minLon.IsNumber || !maxLat.IsNumber || !maxLon.IsNumber)
            {
                return false;
            }

            var candidate = new GeoBoundingBox(mbb.AsArray[0].AsDouble, mbb.AsArray[1].AsDouble, mbb.AsArray[2].AsDouble, mbb.AsArray[3].AsDouble);
            var query = new GeoBoundingBox(minLat.AsDouble, minLon.AsDouble, maxLat.AsDouble, maxLon.AsDouble);

            return candidate.Intersects(query);
        }

        public static BsonValue SPATIAL_NEAR(BsonValue candidate, BsonValue center, BsonValue radiusMeters)
        {
            if (!radiusMeters.IsNumber)
            {
                return false;
            }

            var candidatePoint = ToGeoPoint(candidate);
            var centerPoint = ToGeoPoint(center);

            if (candidatePoint == null || centerPoint == null)
            {
                return false;
            }

            return Spatial.Spatial.Near(candidatePoint, centerPoint, radiusMeters.AsDouble);
        }

        public static BsonValue SPATIAL_WITHIN(BsonValue candidate, BsonValue polygon)
        {
            var shape = ToGeoShape(candidate);
            var area = ToGeoShape(polygon) as GeoPolygon;

            if (shape == null || area == null)
            {
                return false;
            }

            return Spatial.Spatial.Within(shape, area);
        }

        public static BsonValue SPATIAL_INTERSECTS(BsonValue candidate, BsonValue other)
        {
            var left = ToGeoShape(candidate);
            var right = ToGeoShape(other);

            if (left == null || right == null)
            {
                return false;
            }

            return Spatial.Spatial.Intersects(left, right);
        }

        public static BsonValue SPATIAL_CONTAINS_POINT(BsonValue candidate, BsonValue point)
        {
            var shape = ToGeoShape(candidate);
            var geoPoint = ToGeoPoint(point);

            if (shape == null || geoPoint == null)
            {
                return false;
            }

            return Spatial.Spatial.Contains(shape, geoPoint);
        }

        private static GeoShape ToGeoShape(BsonValue value)
        {
            if (value == null || value.IsNull || !value.IsDocument)
            {
                return null;
            }

            return GeoJson.FromBson(value.AsDocument);
        }

        private static GeoPoint ToGeoPoint(BsonValue value)
        {
            var shape = ToGeoShape(value);
            return shape as GeoPoint;
        }
    }
}
