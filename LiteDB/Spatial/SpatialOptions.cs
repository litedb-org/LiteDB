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
        private int _defaultIndexPrecisionBits = 52;
        private double _toleranceDegrees = 1e-9;

        public DistanceFormula Distance { get; set; } = DistanceFormula.Haversine;

        public bool SortNearByDistance { get; set; } = true;

        public int MaxCoveringCells { get; set; } = 32;

        public AngleUnit AngleUnit { get; set; } = AngleUnit.Degrees;

        public int DefaultIndexPrecisionBits
        {
            get => _defaultIndexPrecisionBits;
            set => _defaultIndexPrecisionBits = value;
        }

        public int IndexPrecisionBits
        {
            get => _defaultIndexPrecisionBits;
            set => _defaultIndexPrecisionBits = value;
        }

        public double NumericToleranceDegrees
        {
            get => _toleranceDegrees;
            set => _toleranceDegrees = value;
        }

        public double ToleranceDegrees
        {
            get => _toleranceDegrees;
            set => _toleranceDegrees = value;
        }

        public double BoundingBoxPaddingMeters { get; set; } = 0d;

        public double DistanceToleranceMeters { get; set; } = 0.001d;
    }
}
