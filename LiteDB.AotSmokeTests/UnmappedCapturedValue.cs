namespace LiteDB.AotSmokeTests;

/// <summary>
/// An application type without a generated map. Capturing one in a generated LINQ expression must be rejected.
/// </summary>
internal sealed class UnmappedCapturedValue
{
    public int Threshold { get; set; }
}
