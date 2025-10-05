using LiteDB.Spatial;

namespace LiteDB.Benchmarks.Models.Spatial
{
    public class SpatialDocument
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public GeoPoint Location { get; set; } = new GeoPoint(0, 0);

        public GeoPolygon Region { get; set; } = new GeoPolygon(new[]
        {
            new GeoPoint(0, 0),
            new GeoPoint(0, 0.001),
            new GeoPoint(0.001, 0.001),
            new GeoPoint(0.001, 0),
            new GeoPoint(0, 0)
        });

        public GeoLineString Route { get; set; } = new GeoLineString(new[]
        {
            new GeoPoint(0, 0),
            new GeoPoint(0.001, 0.001)
        });

        internal long _gh { get; set; }

        internal double[] _mbb { get; set; } = Array.Empty<double>();
    }
}
