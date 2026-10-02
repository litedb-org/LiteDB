using System.Text.Json;
using LiteDB.Utils;

namespace LiteDB.Fuzz;

/// <summary>
/// Reachability evidence of one run: <c>markers.json</c> holds the hit count of every
/// <see cref="Reachability"/> marker the run reached. Counts start at zero for each run
/// (each epoch is a fresh process anyway). <c>.github/scripts/check_reachability.py</c>
/// aggregates these files across runs and fails a campaign that never reached a marker
/// its diff declares.
/// </summary>
internal static class FuzzMarkers
{
    internal const string FileName = "markers.json";

    internal static void Reset() => Reachability.Reset();

    internal static void Write(FuzzContext context)
    {
        var path = Path.Combine(context.DirectoryPath, FileName);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            target = context.Target,
            seed = context.Seed,
            hits = Reachability.Snapshot()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
