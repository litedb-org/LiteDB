using System;
using System.Globalization;
using System.IO;

internal static class BenchmarkStartSignal
{
    internal static DateTime Read(string filename)
    {
        // The controller closes the complete value before atomically renaming it.
        // Windows can expose the new name before the rename's DELETE handle closes.
        using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return new DateTime(long.Parse(reader.ReadToEnd(), CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
}
