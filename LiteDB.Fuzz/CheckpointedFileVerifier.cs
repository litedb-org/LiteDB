using System.Security.Cryptography;

namespace LiteDB.Fuzz;

/// <summary>Run the existing raw structural oracle on a quiescent, fully checkpointed fixture.</summary>
internal static class CheckpointedFileVerifier
{
    internal static int Run(FuzzOptions options)
    {
        var filename = Path.GetFullPath(options.Database);
        var log = FileHelper.GetLogFile(filename);
        if (File.Exists(log) && new FileInfo(log).Length != 0)
            throw new InvalidOperationException("Raw fixture validation requires a fully checkpointed database.");
        using var context = new FuzzContext("checkpointed-file", 0, 1, null, options.ArtifactDirectory);
        var before = Hash(filename);
        DatabaseIntegrityVerifier.Verify(context, filename);
        context.Check(before == Hash(filename), "The raw structural oracle modified its input.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(context.Metrics));
        return 0;
    }

    private static string Hash(string filename)
    {
        using var stream = File.OpenRead(filename);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
