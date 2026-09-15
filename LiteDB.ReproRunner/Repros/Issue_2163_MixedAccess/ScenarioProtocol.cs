using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Issue_2163_MixedAccess;

internal sealed record ScenarioPaths(
    string Root,
    string DatabaseAliasRoot,
    string Database,
    string Receipts,
    string OwnerReady,
    string ContenderAttempting,
    string ContenderOutcome,
    string ContenderFatal,
    string ReleaseStarted,
    string OwnerReleased,
    string PostControlOutcome,
    string Verdict)
{
    public static ScenarioPaths Create(string? sharedRoot)
    {
        var root = string.IsNullOrWhiteSpace(sharedRoot)
            ? Path.Combine(AppContext.BaseDirectory, "issue2163")
            : sharedRoot;
        var runIdentifier = Environment.GetEnvironmentVariable("LITEDB_RR_RUN_IDENTIFIER");
        var aliasKey = string.IsNullOrWhiteSpace(runIdentifier)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16]
            : runIdentifier[..Math.Min(runIdentifier.Length, 16)];
        var aliasRoot = Path.Combine(Path.GetTempPath(), "litedb-2163-" + aliasKey);

        return new ScenarioPaths(
            root,
            aliasRoot,
            Path.Combine(aliasRoot, "mixed-access.db"),
            Path.Combine(root, "receipts"),
            Path.Combine(root, "owner-ready.json"),
            Path.Combine(root, "contender-attempting"),
            Path.Combine(root, "contender-outcome.json"),
            Path.Combine(root, "contender-fatal.json"),
            Path.Combine(root, "release-started"),
            Path.Combine(root, "owner-released"),
            Path.Combine(root, "post-control.json"),
            Path.Combine(root, "verdict.json"));
    }

    public void CreateDatabaseAlias()
    {
        if (Directory.Exists(DatabaseAliasRoot) || File.Exists(DatabaseAliasRoot))
        {
            throw new InvalidOperationException($"Database alias '{DatabaseAliasRoot}' already exists.");
        }

        Directory.CreateSymbolicLink(DatabaseAliasRoot, Root);
    }

    public void TryDeleteDatabaseAlias()
    {
        try
        {
            if (Directory.Exists(DatabaseAliasRoot)) Directory.Delete(DatabaseAliasRoot);
        }
        catch
        {
            // The unique temporary alias is diagnostic hygiene, not part of the oracle.
        }
    }
}

internal static class Coordination
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void WriteMarker(string path) => WriteBytes(path, Array.Empty<byte>());

    public static void WriteJson<T>(string path, T value) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    public static bool TryWriteJson<T>(string path, T value)
    {
        if (File.Exists(path)) return false;

        try
        {
            WriteJson(path, value);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    public static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions) ??
        throw new InvalidDataException($"Coordination file '{path}' contained JSON null.");

    public static void WaitFor(string path, TimeSpan timeout, string? failurePath = null)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!File.Exists(path))
        {
            if (failurePath is not null && File.Exists(failurePath))
            {
                var failure = ReadJson<ExceptionDetails>(failurePath);
                throw new InvalidOperationException($"Peer process failed: {failure.Type}: {failure.Message}");
            }

            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for '{path}'.");
            }

            Thread.Sleep(20);
        }
    }

    public static bool TryWaitFor(string path, TimeSpan timeout, string? failurePath = null)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!File.Exists(path))
        {
            if (failurePath is not null && File.Exists(failurePath))
            {
                var failure = ReadJson<ExceptionDetails>(failurePath);
                throw new InvalidOperationException($"Peer process failed: {failure.Type}: {failure.Message}");
            }

            if (stopwatch.Elapsed >= timeout) return false;
            Thread.Sleep(20);
        }

        return true;
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";

        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        File.Move(temporary, path);
    }
}

internal sealed record OwnerReady(int ProcessId, bool TransactionOpen, int VisibleRows);

internal sealed record ExceptionDetails(string Type, string Message, int HResult)
{
    public static ExceptionDetails Capture(Exception exception) =>
        new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.HResult);
}

internal sealed record ContenderOutcome(
    string Kind,
    bool InsertAcknowledged,
    bool CheckpointAcknowledged,
    bool FreshReadBackVerified,
    bool CompletedBeforeRelease,
    bool OwnerReleasedObserved,
    int ProcessId,
    ExceptionDetails? Exception);

internal sealed record PostControlOutcome(
    bool InsertAcknowledged,
    bool CheckpointAcknowledged,
    bool FreshReadBackVerified,
    ExceptionDetails? Exception)
{
    public bool Succeeded =>
        InsertAcknowledged &&
        CheckpointAcknowledged &&
        FreshReadBackVerified &&
        Exception is null;
}

internal sealed record OwnerMutationOutcome(
    bool CommitAcknowledged,
    bool CheckpointAcknowledged,
    bool RejectedBeforeCommit,
    int VisibleRowsBeforeMutation,
    ExceptionDetails? ExpectedCollision,
    ExceptionDetails? UnexpectedFailure)
{
    public bool CollisionRejected => RejectedBeforeCommit || ExpectedCollision is not null;
}

internal sealed record Verdict(int ExitCode, string Marker, string Summary)
{
    public static Verdict HarnessFailure(string summary) => new(20, "HARNESS_ERROR_2163", summary);
}
