using System.Diagnostics.Tracing;

namespace Ab5021Benchmarks;

[EventSource(Name = "LiteDB-ScenarioProfile")]
internal sealed class ProfileMarkers : EventSource
{
    internal static readonly ProfileMarkers Log = new();
    [Event(1)] public void Begin(string scenario) { if (IsEnabled()) WriteEvent(1, scenario); }
    [Event(2)] public void End(string scenario) { if (IsEnabled()) WriteEvent(2, scenario); }
    internal static bool Enabled => Environment.GetEnvironmentVariable("LITEDB_PROFILE") == "1";
}
