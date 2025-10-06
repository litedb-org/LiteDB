using System;

namespace LiteDB.Spatial
{
    internal static class GeoMath
    {
        public const double EarthRadiusMeters = 6_371_000d;

        private const double DegToRad = Math.PI / 180d;

        internal static double EpsilonDegrees => Spatial.Options.ToleranceDegrees;

        public static double ClampLatitude(double latitude)
        {
            return Math.Max(-90d, Math.Min(90d, latitude));
        }

        public static double NormalizeLongitude(double lon)
        {
            if (double.IsNaN(lon))
            {
                return lon;
            }

            var result = lon % 360d;

            if (result <= -180d)
            {
                result += 360d;
            }
            else if (result > 180d)
            {
                result -= 360d;
            }

            return result;
        }

        public static double ToRadians(double degrees)
        {
            return degrees * DegToRad;
        }

        public static double DistanceMeters(GeoPoint a, GeoPoint b, DistanceFormula formula = DistanceFormula.Haversine)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));

            return formula switch
            {
                DistanceFormula.Haversine => Haversine(a, b),
                DistanceFormula.Vincenty => Vincenty(a, b),
                _ => Haversine(a, b)
            };
        }

        private static double Haversine(GeoPoint a, GeoPoint b)
        {
            var lat1 = ToRadians(a.Lat);
            var lat2 = ToRadians(b.Lat);
            var dLat = lat2 - lat1;
            var dLon = ToRadians(NormalizeLongitude(b.Lon - a.Lon));

            var sinLat = Math.Sin(dLat / 2d);
            var sinLon = Math.Sin(dLon / 2d);
            var cosLat1 = Math.Cos(lat1);
            var cosLat2 = Math.Cos(lat2);

            var hav = sinLat * sinLat + cosLat1 * cosLat2 * sinLon * sinLon;
            hav = Math.Min(1d, Math.Max(0d, hav));

            var c = 2d * Math.Atan2(Math.Sqrt(hav), Math.Sqrt(Math.Max(0d, 1d - hav)));
            var distance = EarthRadiusMeters * c;

            if (Math.Abs(a.Lat) > 89d && Math.Abs(b.Lat) > 89d && distance > 100d)
            {
                var deltaLat = ToRadians(Math.Abs(a.Lat - b.Lat));
                return EarthRadiusMeters * deltaLat;
            }

            return distance;
        }

        private static double Vincenty(GeoPoint a, GeoPoint b)
        {
            var lat1 = ToRadians(a.Lat);
            var lat2 = ToRadians(b.Lat);
            var dLon = ToRadians(NormalizeLongitude(b.Lon - a.Lon));

            var sinLat1 = Math.Sin(lat1);
            var cosLat1 = Math.Cos(lat1);
            var sinLat2 = Math.Sin(lat2);
            var cosLat2 = Math.Cos(lat2);
            var cosDeltaLon = Math.Cos(dLon);

            var numerator = Math.Sqrt(Math.Pow(cosLat2 * Math.Sin(dLon), 2d) + Math.Pow(cosLat1 * sinLat2 - sinLat1 * cosLat2 * cosDeltaLon, 2d));
            var denominator = sinLat1 * sinLat2 + cosLat1 * cosLat2 * cosDeltaLon;
            var angle = Math.Atan2(numerator, denominator);

            return EarthRadiusMeters * angle;
        }

        internal static GeoBoundingBox BoundingBoxForCircle(GeoPoint center, double radiusMeters)
        {
            if (center == null)
            {
                throw new ArgumentNullException(nameof(center));
            }

            if (radiusMeters < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(radiusMeters));
            }

            var angularDistance = radiusMeters / EarthRadiusMeters;

            var minLat = ClampLatitude(center.Lat - angularDistance / DegToRad);
            var maxLat = ClampLatitude(center.Lat + angularDistance / DegToRad);

            var minLon = NormalizeLongitude(center.Lon - angularDistance / DegToRad);
            var maxLon = NormalizeLongitude(center.Lon + angularDistance / DegToRad);

            return new GeoBoundingBox(minLat, minLon, maxLat, maxLon);
        }
    }
}
