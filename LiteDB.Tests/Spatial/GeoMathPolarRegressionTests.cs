using System.Collections.Generic;
using FluentAssertions;
using GeographicLib;
using LiteDB.Spatial;
using Xunit;

namespace LiteDB.Tests.Spatial;

public class GeoMathPolarRegressionTests
{
    public static IEnumerable<object[]> HighLatitudeCircleCases()
    {
        yield return new object[] { new GeoPoint(89.0, 0.0), 100_000d, "High-latitude north, 100 km" };
        yield return new object[] { new GeoPoint(-89.0, 30.0), 100_000d, "High-latitude south, 100 km" };
        yield return new object[] { new GeoPoint(70.0, 10.0), 200_000d, "Mid-high north, 200 km" };
        yield return new object[] { new GeoPoint(-70.0, -120.0), 200_000d, "Mid-high south, 200 km" };
    }

    public static IEnumerable<object[]> PoleTouchingCircleCases()
    {
        yield return new object[] { new GeoPoint(89.8, 0.0), 50_000d, "Pole-touching north, 50 km" };
        yield return new object[] { new GeoPoint(-89.8, 0.0), 50_000d, "Pole-touching south, 50 km" };
    }

    [Theory]
    [MemberData(nameof(HighLatitudeCircleCases))]
    public void BoundingBoxForCircle_ShouldContainGeographicLibSamples(GeoPoint center, double radiusMeters, string description)
    {
        var bbox = GeoMath.BoundingBoxForCircle(center, radiusMeters);
        var normalizedMinLon = GeoTestHelpers.NormalizeLon(bbox.MinLon);
        var normalizedMaxLon = GeoTestHelpers.NormalizeLon(bbox.MaxLon);

        for (var azimuth = 0; azimuth < 360; azimuth += 2)
        {
            var boundary = Geodesic.WGS84.Direct(center.Lat, center.Lon, azimuth, radiusMeters);
            var boundaryLon = GeoTestHelpers.NormalizeLon(boundary.Longitude);

            GeoTestHelpers.ContainsWrapAware(bbox, boundary.Latitude, boundaryLon)
                .Should().BeTrue(
                    "Boundary point at azimuth {0}° ({1:F6},{2:F6}) lies outside bbox [{3:F6},{4:F6}]..[{5:F6},{6:F6}] (GeographicLib circle for {7})",
                    azimuth,
                    boundary.Latitude,
                    boundaryLon,
                    bbox.MinLat,
                    normalizedMinLon,
                    bbox.MaxLat,
                    normalizedMaxLon,
                    description);
        }
    }

    [Theory]
    [MemberData(nameof(PoleTouchingCircleCases))]
    public void BoundingBoxForCircle_PoleTouchingCircleShouldSpanAllLongitudes(GeoPoint center, double radiusMeters, string description)
    {
        var bbox = GeoMath.BoundingBoxForCircle(center, radiusMeters);
        var normalizedMinLon = GeoTestHelpers.NormalizeLon(bbox.MinLon);
        var normalizedMaxLon = GeoTestHelpers.NormalizeLon(bbox.MaxLon);

        for (var azimuth = 0; azimuth < 360; azimuth += 2)
        {
            var boundary = Geodesic.WGS84.Direct(center.Lat, center.Lon, azimuth, radiusMeters);
            var boundaryLon = GeoTestHelpers.NormalizeLon(boundary.Longitude);

            GeoTestHelpers.ContainsWrapAware(bbox, boundary.Latitude, boundaryLon)
                .Should().BeTrue(
                    "Pole-touching boundary point at azimuth {0}° ({1:F6},{2:F6}) lies outside bbox [{3:F6},{4:F6}]..[{5:F6},{6:F6}] (GeographicLib circle for {7})",
                    azimuth,
                    boundary.Latitude,
                    boundaryLon,
                    bbox.MinLat,
                    normalizedMinLon,
                    bbox.MaxLat,
                    normalizedMaxLon,
                    description);
        }

        normalizedMinLon.Should().BeApproximately(-180d, 1e-6,
            "Circles touching a pole should span all longitudes: expected -180° min lon (GeographicLib circle for {0})",
            description);

        normalizedMaxLon.Should().BeApproximately(180d, 1e-6,
            "Circles touching a pole should span all longitudes: expected 180° max lon (GeographicLib circle for {0})",
            description);
    }

    public static IEnumerable<object[]> PolarDistanceCases()
    {
        yield return new object[] { new GeoPoint(89.5, 0.0), new GeoPoint(89.5, 180.0), "Northern hemisphere" };
        yield return new object[] { new GeoPoint(-89.5, 0.0), new GeoPoint(-89.5, 180.0), "Southern hemisphere" };
    }

    [Theory]
    [MemberData(nameof(PolarDistanceCases))]
    public void DistanceMeters_ShouldMatchGeographicLibNearPoles(GeoPoint a, GeoPoint b, string description)
    {
        var expected = Geodesic.WGS84.Inverse(a.Lat, a.Lon, b.Lat, b.Lon).Distance;
        var haversine = GeoMath.DistanceMeters(a, b, DistanceFormula.Haversine);
        var vincenty = GeoMath.DistanceMeters(a, b, DistanceFormula.Vincenty);

        haversine.Should().BeApproximately(expected, 1.0,
            "Haversine near pole diverges: expected ~{0:F3} m (GeographicLib), actual {1:F3} m ({2})",
            expected,
            haversine,
            description);

        vincenty.Should().BeApproximately(expected, 5.0,
            "Vincenty should stay close to GeographicLib near poles for control pair ({0})",
            description);
    }
}

