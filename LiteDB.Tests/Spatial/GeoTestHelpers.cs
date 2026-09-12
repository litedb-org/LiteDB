using LiteDB.Spatial;

namespace LiteDB.Tests.Spatial;

internal static class GeoTestHelpers
{
    private const double Epsilon = 1e-12;

    public static double NormalizeLon(double lon)
    {
        if (double.IsNaN(lon) || double.IsInfinity(lon))
        {
            return lon;
        }

        var result = lon % 360d;

        if (result < -180d)
        {
            result += 360d;
        }
        else if (result >= 180d)
        {
            result -= 360d;
        }

        return result;
    }

    public static bool ContainsWrapAware(GeoBoundingBox box, double lat, double lon)
    {
        if (lat < box.MinLat - Epsilon || lat > box.MaxLat + Epsilon)
        {
            return false;
        }

        var normalizedLon = NormalizeLon(lon);
        var minLon = NormalizeLon(box.MinLon);
        var maxLon = NormalizeLon(box.MaxLon);

        if (box.SpansAllLongitudes)
        {
            return true;
        }

        if (minLon <= maxLon + Epsilon)
        {
            return normalizedLon >= minLon - Epsilon && normalizedLon <= maxLon + Epsilon;
        }

        return normalizedLon >= minLon - Epsilon || normalizedLon <= maxLon + Epsilon;
    }
}

