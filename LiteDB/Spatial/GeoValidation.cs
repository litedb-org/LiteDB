using System;

namespace LiteDB.Spatial
{
    internal static class GeoValidation
    {
        private const double LatitudeMin = -90d;
        private const double LatitudeMax = 90d;
        private const double LongitudeMin = -180d;
        private const double LongitudeMax = 180d;

        public static void EnsureValidCoordinate(double lat, double lon)
        {
            if (lat < LatitudeMin || lat > LatitudeMax)
            {
                throw new ArgumentOutOfRangeException(nameof(lat), $"Latitude must be between {LatitudeMin} and {LatitudeMax}");
            }

            if (lon < LongitudeMin || lon > LongitudeMax)
            {
                throw new ArgumentOutOfRangeException(nameof(lon), $"Longitude must be between {LongitudeMin} and {LongitudeMax}");
            }
        }
    }
}
