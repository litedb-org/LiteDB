using System;
using System.Linq;
using LiteDB.Spatial;

namespace LiteDB
{
    public static class SpatialExpressions
    {
        public static bool Near(GeoPoint point, GeoPoint center, double radiusMeters)
        {
            return Near(point, center, radiusMeters, Spatial.Spatial.Options.Distance);
        }

        public static bool Near(GeoPoint point, GeoPoint center, double radiusMeters, DistanceFormula formula)
        {
            if (point == null || center == null)
            {
                return false;
            }

            if (radiusMeters < 0d)
            {
                return false;
            }

            var distance = GeoMath.DistanceMeters(point, center, formula);
            return distance <= radiusMeters + Spatial.Spatial.GetDistanceToleranceMeters();
        }

        public static bool Within(GeoShape shape, GeoPolygon polygon)
        {
            if (shape == null || polygon == null)
            {
                return false;
            }

            return shape switch
            {
                GeoPoint point => Geometry.ContainsPoint(polygon, point),
                GeoPolygon other => Geometry.Intersects(polygon, other) && other.Outer.All(p => Geometry.ContainsPoint(polygon, p)),
                GeoLineString line => line.Points.All(p => Geometry.ContainsPoint(polygon, p)),
                _ => false
            };
        }

        public static bool Intersects(GeoShape shape, GeoShape other)
        {
            if (shape == null || other == null)
            {
                return false;
            }

            return shape switch
            {
                GeoPoint point when other is GeoPolygon polygon => Geometry.ContainsPoint(polygon, point),
                GeoPoint point when other is GeoLineString line => Geometry.LineContainsPoint(line, point),
                GeoPoint point when other is GeoPoint otherPoint => Math.Abs(point.Lat - otherPoint.Lat) < GeoMath.EpsilonDegrees && Math.Abs(point.Lon - otherPoint.Lon) < GeoMath.EpsilonDegrees,
                GeoLineString line when other is GeoPolygon polygon => Geometry.Intersects(line, polygon),
                GeoLineString line when other is GeoLineString otherLine => Geometry.Intersects(line, otherLine),
                GeoLineString line when other is GeoPoint point => Geometry.LineContainsPoint(line, point),
                GeoPolygon polygon when other is GeoPolygon otherPolygon => Geometry.Intersects(polygon, otherPolygon),
                GeoPolygon polygon when other is GeoLineString line => Geometry.Intersects(line, polygon),
                GeoPolygon polygon when other is GeoPoint point => Geometry.ContainsPoint(polygon, point),
                _ => false
            };
        }

        public static bool Contains(GeoShape shape, GeoPoint point)
        {
            if (shape == null || point == null)
            {
                return false;
            }

            return shape switch
            {
                GeoPolygon polygon => Geometry.ContainsPoint(polygon, point),
                GeoLineString line => Geometry.LineContainsPoint(line, point),
                GeoPoint candidate => Math.Abs(candidate.Lat - point.Lat) < GeoMath.EpsilonDegrees && Math.Abs(candidate.Lon - point.Lon) < GeoMath.EpsilonDegrees,
                _ => false
            };
        }

        public static bool WithinBoundingBox(GeoPoint point, double minLat, double minLon, double maxLat, double maxLon)
        {
            if (point == null)
            {
                return false;
            }

            var box = new GeoBoundingBox(minLat, minLon, maxLat, maxLon);
            return box.Contains(point);
        }
    }
}
