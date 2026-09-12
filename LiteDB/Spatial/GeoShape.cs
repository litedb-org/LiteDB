using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace LiteDB.Spatial
{
    public abstract record GeoShape
    {
        internal abstract GeoBoundingBox GetBoundingBox();
    }

    public sealed record GeoPoint : GeoShape
    {
        public double Lat { get; }

        public double Lon { get; }

        public GeoPoint(double lat, double lon)
        {
            GeoValidation.EnsureValidCoordinate(lat, lon);

            Lat = GeoMath.ClampLatitude(lat);
            Lon = GeoMath.NormalizeLongitude(lon);
        }

        internal override GeoBoundingBox GetBoundingBox()
        {
            return new GeoBoundingBox(Lat, Lon, Lat, Lon);
        }

        public GeoPoint Normalize()
        {
            return new GeoPoint(Lat, Lon);
        }

        public override string ToString()
        {
            return $"({Lat:F6}, {Lon:F6})";
        }
    }

    public sealed record GeoLineString : GeoShape
    {
        public IReadOnlyList<GeoPoint> Points { get; }

        public GeoLineString(IReadOnlyList<GeoPoint> points)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (points.Count < 2)
            {
                throw new ArgumentException("LineString requires at least two points", nameof(points));
            }

            Points = new ReadOnlyCollection<GeoPoint>(points.Select(p => p ?? throw new ArgumentNullException(nameof(points), "LineString points cannot contain null"))
                .Select(p => p.Normalize()).ToList());
        }

        internal override GeoBoundingBox GetBoundingBox()
        {
            return GeoBoundingBox.FromPoints(Points);
        }
    }

    public sealed record GeoPolygon : GeoShape
    {
        public IReadOnlyList<GeoPoint> Outer { get; }

        public IReadOnlyList<IReadOnlyList<GeoPoint>> Holes { get; }

        public GeoPolygon(IReadOnlyList<GeoPoint> outer, IReadOnlyList<IReadOnlyList<GeoPoint>> holes = null)
        {
            if (outer == null)
            {
                throw new ArgumentNullException(nameof(outer));
            }

            if (outer.Count < 4)
            {
                throw new ArgumentException("Polygon outer ring must contain at least four points including closure", nameof(outer));
            }

            var normalizedOuter = NormalizeRing(outer, nameof(outer));

            Outer = new ReadOnlyCollection<GeoPoint>(normalizedOuter);

            if (holes == null)
            {
                Holes = Array.Empty<IReadOnlyList<GeoPoint>>();
            }
            else
            {
                var normalizedHoles = holes.Select((hole, index) =>
                {
                    var points = NormalizeRing(hole, $"holes[{index}]");
                    return (IReadOnlyList<GeoPoint>)new ReadOnlyCollection<GeoPoint>(points);
                }).ToList();

                Holes = new ReadOnlyCollection<IReadOnlyList<GeoPoint>>(normalizedHoles);
            }

            foreach (var hole in Holes)
            {
                if (!Geometry.IsRingInside(Outer, hole))
                {
                    throw new ArgumentException("Polygon hole must lie within the outer ring", nameof(holes));
                }

                foreach (var other in Holes)
                {
                    if (!ReferenceEquals(hole, other) && Geometry.RingsOverlap(hole, other))
                    {
                        throw new ArgumentException("Polygon holes must not overlap", nameof(holes));
                    }
                }
            }
        }

        internal override GeoBoundingBox GetBoundingBox()
        {
            var allPoints = new List<GeoPoint>(Outer.Count + Holes.Sum(h => h.Count));
            allPoints.AddRange(Outer);
            foreach (var hole in Holes)
            {
                allPoints.AddRange(hole);
            }

            return GeoBoundingBox.FromPoints(allPoints);
        }

        private static List<GeoPoint> NormalizeRing(IReadOnlyList<GeoPoint> ring, string argumentName)
        {
            if (ring == null)
            {
                throw new ArgumentNullException(argumentName);
            }

            if (ring.Count < 4)
            {
                throw new ArgumentException("Polygon rings must contain at least four points including closure", argumentName);
            }

            var normalized = ring.Select(p => p ?? throw new ArgumentNullException(argumentName, "Polygon ring point cannot be null"))
                .Select(p => p.Normalize()).ToList();

            if (!Geometry.IsRingClosed(normalized))
            {
                throw new ArgumentException("Polygon rings must be closed (first point equals last point)", argumentName);
            }

            if (Geometry.HasSelfIntersection(normalized))
            {
                throw new ArgumentException("Polygon rings must not self-intersect", argumentName);
            }

            return normalized;
        }
    }
}
