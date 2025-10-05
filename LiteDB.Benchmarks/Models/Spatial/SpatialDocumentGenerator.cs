using System;
using System.Collections.Generic;
using LiteDB.Spatial;

namespace LiteDB.Benchmarks.Models.Spatial
{
    internal static class SpatialDocumentGenerator
    {
        public static List<SpatialDocument> Generate(int count)
        {
            var random = new Random(1337);
            var documents = new List<SpatialDocument>(count);

            for (var i = 0; i < count; i++)
            {
                var lat = random.NextDouble() * 0.8 - 0.4;
                var lon = random.NextDouble() * 0.8 - 0.4;
                var location = new GeoPoint(lat, lon);

                var region = BuildSquare(location, random.NextDouble() * 0.05 + 0.01);
                var route = BuildRoute(location, random);

                documents.Add(new SpatialDocument
                {
                    Id = i + 1,
                    Name = $"Place #{i + 1}",
                    Location = location,
                    Region = region,
                    Route = route
                });
            }

            return documents;
        }

        public static GeoPolygon BuildSearchPolygon(double centerLat, double centerLon, double radiusDegrees)
        {
            var center = new GeoPoint(centerLat, centerLon);
            return BuildSquare(center, radiusDegrees);
        }

        private static GeoPolygon BuildSquare(GeoPoint center, double halfExtent)
        {
            var minLat = GeoMath.ClampLatitude(center.Lat - halfExtent);
            var maxLat = GeoMath.ClampLatitude(center.Lat + halfExtent);
            var minLon = GeoMath.NormalizeLongitude(center.Lon - halfExtent);
            var maxLon = GeoMath.NormalizeLongitude(center.Lon + halfExtent);

            var points = new List<GeoPoint>
            {
                new GeoPoint(maxLat, minLon),
                new GeoPoint(maxLat, maxLon),
                new GeoPoint(minLat, maxLon),
                new GeoPoint(minLat, minLon),
                new GeoPoint(maxLat, minLon)
            };

            return new GeoPolygon(points);
        }

        private static GeoLineString BuildRoute(GeoPoint start, Random random)
        {
            var midLat = start.Lat + random.NextDouble() * 0.1 - 0.05;
            var midLon = start.Lon + random.NextDouble() * 0.1 - 0.05;
            var endLat = start.Lat + random.NextDouble() * 0.2 - 0.1;
            var endLon = start.Lon + random.NextDouble() * 0.2 - 0.1;

            var points = new List<GeoPoint>
            {
                start,
                new GeoPoint(midLat, midLon),
                new GeoPoint(endLat, endLon)
            };

            return new GeoLineString(points);
        }
    }
}
