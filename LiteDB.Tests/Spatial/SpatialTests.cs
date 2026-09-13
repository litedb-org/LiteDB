using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;
using LiteDB.Spatial;
using Xunit;
using SpatialApi = LiteDB.Spatial.Spatial;

namespace LiteDB.Tests.Spatial
{
    public class SpatialTests
    {
        [Fact]
        public void Near_Returns_Ordered_Within_Radius()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");
            SpatialApi.EnsurePointIndex(col, x => x.Location);

            var center = new GeoPoint(48.2082, 16.3738);
            var a = new Place { Name = "A", Location = new GeoPoint(48.215, 16.355) };
            var b = new Place { Name = "B", Location = new GeoPoint(48.185, 16.38) };
            var c = new Place { Name = "C", Location = new GeoPoint(48.300, 16.450) };
            col.Insert(new[] { a, b, c });

            var result = SpatialApi.Near(col, x => x.Location, center, 5_000).ToList();

            Assert.Equal(new[] { "A", "B" }, result.Select(x => x.Name));
            Assert.True(result.SequenceEqual(result.OrderBy(x => GeoMath.DistanceMeters(center, x.Location, DistanceFormula.Haversine))));
        }

        [Fact]
        public void BoundingBox_Crosses_Antimeridian()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");
            SpatialApi.EnsurePointIndex(col, x => x.Location);

            col.Insert(new Place { Name = "W", Location = new GeoPoint(0, -179.5) });
            col.Insert(new Place { Name = "E", Location = new GeoPoint(0, 179.5) });

            var result = SpatialApi.WithinBoundingBox(col, x => x.Location, -10, 170, 10, -170).ToList();

            Assert.Contains(result, p => p.Name == "W");
            Assert.Contains(result, p => p.Name == "E");
        }

        [Theory]
        [InlineData(89.9, 0, 89.9, 90)]
        [InlineData(-89.9, 0, -89.9, -90)]
        public void GreatCircle_Is_Stable_Near_Poles(double lat1, double lon1, double lat2, double lon2)
        {
            var d = GeoMath.DistanceMeters(new GeoPoint(lat1, lon1), new GeoPoint(lat2, lon2), DistanceFormula.Haversine);
            Assert.InRange(d, 0, 100);
        }

        [Fact]
        public void GeoJson_RoundTrip_Point()
        {
            var point = new GeoPoint(48.2082, 16.3738);
            var json = GeoJson.Serialize(point);
            var back = GeoJson.Deserialize<GeoPoint>(json);

            Assert.Equal(point.Lat, back.Lat, 10);
            Assert.Equal(point.Lon, back.Lon, 10);
        }

        [Fact]
        public void Within_Polygon_Excludes_Hole()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");
            SpatialApi.EnsureShapeIndex(col, x => x.Location);

            var outer = new[]
            {
                new GeoPoint(48.25, 16.30),
                new GeoPoint(48.25, 16.45),
                new GeoPoint(48.15, 16.45),
                new GeoPoint(48.15, 16.30),
                new GeoPoint(48.25, 16.30)
            };

            var hole = new[]
            {
                new GeoPoint(48.22, 16.36),
                new GeoPoint(48.22, 16.39),
                new GeoPoint(48.18, 16.39),
                new GeoPoint(48.18, 16.36),
                new GeoPoint(48.22, 16.36)
            };

            var poly = new GeoPolygon(outer, new[] { hole });
            var inHole = new Place { Name = "Edge", Location = new GeoPoint(48.20, 16.37) };
            var outside = new Place { Name = "Outside", Location = new GeoPoint(48.30, 16.50) };
            col.Insert(new[] { inHole, outside });

            var result = SpatialApi.Within(col, x => x.Location, poly).ToList();

            Assert.DoesNotContain(result, p => p.Name == "Edge");
            Assert.DoesNotContain(result, p => p.Name == "Outside");
        }

        [Fact]
        public void GeoHash_Recomputes_On_Update()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");
            SpatialApi.EnsurePointIndex(col, x => x.Location);

            var place = new Place { Name = "P", Location = new GeoPoint(48.2, 16.37) };
            col.Insert(place);

            var h1 = col.FindOne(x => x.Name == "P")._gh;

            place.Location = new GeoPoint(48.21, 16.38);
            col.Update(place);

            var h2 = col.FindById(place.Id)._gh;

            Assert.NotEqual(h1, h2);
        }

        [Theory]
        [InlineData(91, 0)]
        [InlineData(-91, 0)]
        [InlineData(0, 181)]
        [InlineData(0, -181)]
        public void Invalid_Coordinates_Throw(double lat, double lon)
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");

            Assert.Throws<ArgumentOutOfRangeException>(() => col.Insert(new Place { Name = "X", Location = new GeoPoint(lat, lon) }));
        }

        [Fact]
        public void Intersects_Line_Touches_Polygon_Edge()
        {
            using var db = new LiteDatabase(":memory:");
            var roads = db.GetCollection<Road>("roads");
            SpatialApi.EnsureShapeIndex(roads, r => r.Path);

            var line = new GeoLineString(new List<GeoPoint>
            {
                new GeoPoint(0.0, -1.0),
                new GeoPoint(0.0, 2.0)
            });

            var road = new Road { Name = "Cross", Path = line };
            roads.Insert(road);

            var squarePoints = new List<GeoPoint>
            {
                new GeoPoint(0.5, -0.5),
                new GeoPoint(0.5, 1.5),
                new GeoPoint(-0.5, 1.5),
                new GeoPoint(-0.5, -0.5),
                new GeoPoint(0.5, -0.5)
            };

            var square = new GeoPolygon(squarePoints);

            var hits = SpatialApi.Intersects(roads, r => r.Path, square).ToList();

            Assert.NotEmpty(hits);
        }

        [Fact]
        public void EnsurePointIndex_Persists_Precision_Metadata()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");

            SpatialApi.EnsurePointIndex(col, x => x.Location, 40);

            var meta = db.GetCollection("_spatial_meta").FindAll().ToList();

            Assert.Single(meta);
            Assert.Equal(40, meta[0]["precisionBits"].AsInt32);
        }

        [Fact]
        public void Linq_Near_Uses_Spatial_Operator()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");
            SpatialApi.EnsurePointIndex(col, x => x.Location);

            col.Insert(new[]
            {
                new Place { Name = "Center", Location = new GeoPoint(48.2082, 16.3738) },
                new Place { Name = "Far", Location = new GeoPoint(48.35, 16.7) }
            });

            var center = new GeoPoint(48.2082, 16.3738);

            var results = col.Query()
                .Where(p => SpatialApi.Near(p.Location, center, 1_000))
                .ToList();

            Assert.Single(results);
            Assert.Equal("Center", results[0].Name);
        }

        [Fact]
        public void Linq_Within_Uses_Spatial_Operator()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Place>("p");
            SpatialApi.EnsureShapeIndex(col, x => x.Location);

            var polygon = new GeoPolygon(new[]
            {
                new GeoPoint(48.25, 16.30),
                new GeoPoint(48.25, 16.45),
                new GeoPoint(48.15, 16.45),
                new GeoPoint(48.15, 16.30),
                new GeoPoint(48.25, 16.30)
            });

            col.Insert(new[]
            {
                new Place { Name = "Inside", Location = new GeoPoint(48.21, 16.37) },
                new Place { Name = "Outside", Location = new GeoPoint(48.30, 16.60) }
            });

            var results = col.Query()
                .Where(p => SpatialApi.Within(p.Location, polygon))
                .ToList();

            Assert.Single(results);
            Assert.Equal("Inside", results[0].Name);
        }

        private class Place
        {
            public ObjectId Id { get; set; }

            public string Name { get; set; } = string.Empty;

            public GeoPoint Location { get; set; } = new GeoPoint(0, 0);

            internal long _gh { get; set; }

            internal double[] _mbb { get; set; } = Array.Empty<double>();
        }

        private class Road
        {
            public ObjectId Id { get; set; }

            public string Name { get; set; } = string.Empty;

            public GeoLineString Path { get; set; } = new GeoLineString(new[]
            {
                new GeoPoint(0, 0),
                new GeoPoint(0, 0.1)
            });

            internal double[] _mbb { get; set; } = Array.Empty<double>();
        }
    }
}
