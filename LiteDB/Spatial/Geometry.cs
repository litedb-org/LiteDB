using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Spatial
{
    internal static class Geometry
    {
        private const double Epsilon = 1e-9;

        public static bool ContainsPoint(GeoPolygon polygon, GeoPoint point)
        {
            if (polygon == null)
            {
                throw new ArgumentNullException(nameof(polygon));
            }

            if (point == null)
            {
                throw new ArgumentNullException(nameof(point));
            }

            if (!IsPointInRing(polygon.Outer, point))
            {
                return false;
            }

            foreach (var hole in polygon.Holes)
            {
                if (IsPointInRing(hole, point))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool Intersects(GeoLineString line, GeoPolygon polygon)
        {
            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            if (polygon == null)
            {
                throw new ArgumentNullException(nameof(polygon));
            }

            for (var i = 0; i < line.Points.Count - 1; i++)
            {
                var segmentStart = line.Points[i];
                var segmentEnd = line.Points[i + 1];

                if (ContainsPoint(polygon, segmentStart) || ContainsPoint(polygon, segmentEnd))
                {
                    return true;
                }

                if (IntersectsRing(segmentStart, segmentEnd, polygon.Outer))
                {
                    return true;
                }

                foreach (var hole in polygon.Holes)
                {
                    if (IntersectsRing(segmentStart, segmentEnd, hole))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool Intersects(GeoPolygon a, GeoPolygon b)
        {
            if (a == null)
            {
                throw new ArgumentNullException(nameof(a));
            }

            if (b == null)
            {
                throw new ArgumentNullException(nameof(b));
            }

            if (!a.GetBoundingBox().Intersects(b.GetBoundingBox()))
            {
                return false;
            }

            if (a.Outer.Any(p => ContainsPoint(b, p)) || b.Outer.Any(p => ContainsPoint(a, p)))
            {
                return true;
            }

            return RingsIntersect(a.Outer, b.Outer);
        }

        public static bool Intersects(GeoLineString a, GeoLineString b)
        {
            for (var i = 0; i < a.Points.Count - 1; i++)
            {
                for (var j = 0; j < b.Points.Count - 1; j++)
                {
                    if (SegmentsIntersect(a.Points[i], a.Points[i + 1], b.Points[j], b.Points[j + 1]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool RingsOverlap(IReadOnlyList<GeoPoint> a, IReadOnlyList<GeoPoint> b)
        {
            return RingsIntersect(a, b) || a.Any(p => IsPointInRing(b, p)) || b.Any(p => IsPointInRing(a, p));
        }

        public static bool HasSelfIntersection(IReadOnlyList<GeoPoint> ring)
        {
            for (var i = 0; i < ring.Count - 1; i++)
            {
                for (var j = i + 1; j < ring.Count - 1; j++)
                {
                    if (Math.Abs(i - j) <= 1)
                    {
                        continue;
                    }

                    if (i == 0 && j == ring.Count - 2)
                    {
                        continue;
                    }

                    if (SharesEndpoint(ring[i], ring[i + 1], ring[j], ring[j + 1]))
                    {
                        continue;
                    }

                    if (SegmentsIntersect(ring[i], ring[i + 1], ring[j], ring[j + 1]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsRingClosed(IReadOnlyList<GeoPoint> ring)
        {
            if (ring.Count < 4)
            {
                return false;
            }

            var first = ring[0];
            var last = ring[ring.Count - 1];

            return Math.Abs(first.Lat - last.Lat) < Epsilon && Math.Abs(first.Lon - last.Lon) < Epsilon;
        }

        public static bool IsRingInside(IReadOnlyList<GeoPoint> outer, IReadOnlyList<GeoPoint> inner)
        {
            return inner.All(p => IsPointInRing(outer, p));
        }

        public static bool LineContainsPoint(GeoLineString line, GeoPoint point)
        {
            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            if (point == null)
            {
                throw new ArgumentNullException(nameof(point));
            }

            for (var i = 0; i < line.Points.Count - 1; i++)
            {
                var start = line.Points[i];
                var end = line.Points[i + 1];

                if (OnSegment(start, end, point) && Math.Abs(Direction(start, end, point)) < Epsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPointInRing(IReadOnlyList<GeoPoint> ring, GeoPoint point)
        {
            var inside = false;

            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var pi = ring[i];
                var pj = ring[j];

                var intersects = ((pi.Lat > point.Lat) != (pj.Lat > point.Lat)) &&
                    (point.Lon < (pj.Lon - pi.Lon) * (point.Lat - pi.Lat) / (pj.Lat - pi.Lat + double.Epsilon) + pi.Lon);

                if (intersects)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static bool IntersectsRing(GeoPoint a, GeoPoint b, IReadOnlyList<GeoPoint> ring)
        {
            for (var i = 0; i < ring.Count - 1; i++)
            {
                if (SegmentsIntersect(a, b, ring[i], ring[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RingsIntersect(IReadOnlyList<GeoPoint> a, IReadOnlyList<GeoPoint> b)
        {
            for (var i = 0; i < a.Count - 1; i++)
            {
                for (var j = 0; j < b.Count - 1; j++)
                {
                    if (SegmentsIntersect(a[i], a[i + 1], b[j], b[j + 1]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool SegmentsIntersect(GeoPoint a1, GeoPoint a2, GeoPoint b1, GeoPoint b2)
        {
            var d1 = Direction(a1, a2, b1);
            var d2 = Direction(a1, a2, b2);
            var d3 = Direction(b1, b2, a1);
            var d4 = Direction(b1, b2, a2);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return true;
            }

            if (Math.Abs(d1) < Epsilon && OnSegment(a1, a2, b1)) return true;
            if (Math.Abs(d2) < Epsilon && OnSegment(a1, a2, b2)) return true;
            if (Math.Abs(d3) < Epsilon && OnSegment(b1, b2, a1)) return true;
            if (Math.Abs(d4) < Epsilon && OnSegment(b1, b2, a2)) return true;

            return false;
        }

        private static double Direction(GeoPoint a, GeoPoint b, GeoPoint c)
        {
            return (b.Lon - a.Lon) * (c.Lat - a.Lat) - (b.Lat - a.Lat) * (c.Lon - a.Lon);
        }

        private static bool SharesEndpoint(GeoPoint a1, GeoPoint a2, GeoPoint b1, GeoPoint b2)
        {
            return IsSamePoint(a1, b1) || IsSamePoint(a1, b2) || IsSamePoint(a2, b1) || IsSamePoint(a2, b2);
        }

        private static bool IsSamePoint(GeoPoint a, GeoPoint b)
        {
            return Math.Abs(a.Lat - b.Lat) < Epsilon && Math.Abs(a.Lon - b.Lon) < Epsilon;
        }

        private static bool OnSegment(GeoPoint a, GeoPoint b, GeoPoint c)
        {
            return c.Lat >= Math.Min(a.Lat, b.Lat) - Epsilon &&
                   c.Lat <= Math.Max(a.Lat, b.Lat) + Epsilon &&
                   c.Lon >= Math.Min(a.Lon, b.Lon) - Epsilon &&
                   c.Lon <= Math.Max(a.Lon, b.Lon) + Epsilon;
        }
    }
}
