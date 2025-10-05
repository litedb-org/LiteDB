namespace LiteDB.Spatial
{
    public enum DistanceFormula
    {
        Haversine,
        Vincenty
    }

    public enum AngleUnit
    {
        Degrees,
        Radians
    }

    public sealed class SpatialOptions
    {
        public DistanceFormula Distance { get; set; } = DistanceFormula.Haversine;

        public bool SortNearByDistance { get; set; } = true;

        public int MaxCoveringCells { get; set; } = 32;

        public AngleUnit AngleUnit { get; set; } = AngleUnit.Degrees;

        public int IndexPrecisionBits { get; set; } = 52;

        public double NumericToleranceDegrees { get; set; } = 1e-9;
    }
}
