using System;

namespace LiteDB.Spatial
{
    internal static class GeoMath
    {
        public const double EarthRadiusMeters = 6_371_000d;

        private const double DegToRad = Math.PI / 180d;
        private const double RadToDeg = 180d / Math.PI;

        private const double Wgs84EquatorialRadius = 6_378_137d;
        private const double Wgs84Flattening = 1d / 298.257223563d;
        private const double Wgs84PolarRadius = Wgs84EquatorialRadius * (1d - Wgs84Flattening);

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

        private static double Haversine(GeoPoint a, GeoPoint b, bool allowVincentyFallback = true)
        {
            if (allowVincentyFallback && Math.Abs(a.Lat) > 89d && Math.Abs(b.Lat) > 89d)
            {
                var lonDifference = Math.Abs(NormalizeLongitude(b.Lon - a.Lon));

                if (lonDifference > 135d)
                {
                    return Vincenty(a, b);
                }

                var deltaLat = ToRadians(Math.Abs(a.Lat - b.Lat));
                return EarthRadiusMeters * deltaLat;
            }

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

            return EarthRadiusMeters * c;
        }

        private static double Vincenty(GeoPoint a, GeoPoint b)
        {
            var phi1 = ToRadians(a.Lat);
            var phi2 = ToRadians(b.Lat);
            var lambda = ToRadians(NormalizeLongitude(b.Lon - a.Lon));

            var f = Wgs84Flattening;
            var aRadius = Wgs84EquatorialRadius;
            var bRadius = Wgs84PolarRadius;

            var tanU1 = (1d - f) * Math.Tan(phi1);
            var cosU1 = 1d / Math.Sqrt(1d + tanU1 * tanU1);
            var sinU1 = tanU1 * cosU1;

            var tanU2 = (1d - f) * Math.Tan(phi2);
            var cosU2 = 1d / Math.Sqrt(1d + tanU2 * tanU2);
            var sinU2 = tanU2 * cosU2;

            var lambdaIter = lambda;
            double lambdaPrev;

            const int maxIterations = 100;
            var iteration = 0;

            double sinSigma;
            double cosSigma;
            double sigma;
            double cosSqAlpha;
            double cos2SigmaM = 0d;

            do
            {
                var sinLambda = Math.Sin(lambdaIter);
                var cosLambda = Math.Cos(lambdaIter);

                var term1 = cosU2 * sinLambda;
                var term2 = cosU1 * sinU2 - sinU1 * cosU2 * cosLambda;

                sinSigma = Math.Sqrt(term1 * term1 + term2 * term2);

                if (sinSigma == 0d)
                {
                    return 0d;
                }

                cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
                sigma = Math.Atan2(sinSigma, cosSigma);

                var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
                cosSqAlpha = 1d - sinAlpha * sinAlpha;

                if (cosSqAlpha != 0d)
                {
                    cos2SigmaM = cosSigma - 2d * sinU1 * sinU2 / cosSqAlpha;
                }
                else
                {
                    cos2SigmaM = 0d;
                }

                var c = f / 16d * cosSqAlpha * (4d + f * (4d - 3d * cosSqAlpha));
                lambdaPrev = lambdaIter;
                lambdaIter = lambda + (1d - c) * f * sinAlpha * (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1d + 2d * cos2SigmaM * cos2SigmaM)));

                iteration++;
            }
            while (Math.Abs(lambdaIter - lambdaPrev) > 1e-12 && iteration < maxIterations);

            if (iteration == maxIterations)
            {
                return Haversine(a, b, allowVincentyFallback: false);
            }

            var uSq = cosSqAlpha * (aRadius * aRadius - bRadius * bRadius) / (bRadius * bRadius);
            var bigA = 1d + uSq / 16384d * (4096d + uSq * (-768d + uSq * (320d - 175d * uSq)));
            var bigB = uSq / 1024d * (256d + uSq * (-128d + uSq * (74d - 47d * uSq)));

            var deltaSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4d * (cosSigma * (-1d + 2d * cos2SigmaM * cos2SigmaM) - bigB / 6d * cos2SigmaM * (-3d + 4d * sinSigma * sinSigma) * (-3d + 4d * cos2SigmaM * cos2SigmaM)));
            var s = bRadius * bigA * (sigma - deltaSigma);

            return s;
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

            var centerLatRadians = ToRadians(center.Lat);
            var minLatRadians = centerLatRadians - angularDistance;
            var maxLatRadians = centerLatRadians + angularDistance;

            var minLat = ClampLatitude(minLatRadians * RadToDeg);
            var maxLat = ClampLatitude(maxLatRadians * RadToDeg);

            double minLon;
            double maxLon;

            if (minLatRadians <= -Math.PI / 2d || maxLatRadians >= Math.PI / 2d)
            {
                minLon = -180d;
                maxLon = 180d;
            }
            else
            {
                var cosLat = Math.Cos(centerLatRadians);

                if (cosLat <= 0d)
                {
                    minLon = -180d;
                    maxLon = 180d;
                }
                else
                {
                    var sinAngular = Math.Sin(angularDistance);
                    var ratio = Math.Min(1d, Math.Max(-1d, sinAngular / cosLat));
                    var deltaLon = Math.Asin(ratio) * RadToDeg;

                    minLon = NormalizeLongitude(center.Lon - deltaLon);
                    maxLon = NormalizeLongitude(center.Lon + deltaLon);
                }
            }

            return new GeoBoundingBox(minLat, minLon, maxLat, maxLon);
        }
    }
}
