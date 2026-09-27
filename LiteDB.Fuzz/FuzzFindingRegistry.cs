using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteDB.Fuzz;

internal enum FuzzFindingStatus
{
    Unknown,
    Known,
    Expected,
    Fixed
}

internal sealed record FuzzFinding(string Target, string Fingerprint, int Issue, FuzzFindingStatus Status);

internal sealed record FuzzFindingResolution(string Target, string FailureId, string Fingerprint,
    FuzzFindingStatus Status, int? Issue)
{
    internal bool AllowsDiscoveryToContinue => Status is FuzzFindingStatus.Known or FuzzFindingStatus.Expected;
}

internal static partial class FuzzFindingRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static RunResult Classify(RunResult result)
    {
        var failureId = ReadFailureId(result.Directory);
        var resolution = Resolve(result.Target, failureId, DefaultPath());
        File.WriteAllText(Path.Combine(result.Directory, "finding.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                target = resolution.Target,
                failureId = resolution.FailureId,
                fingerprint = resolution.Fingerprint,
                status = resolution.Status.ToString().ToLowerInvariant(),
                issue = resolution.Issue
            }, JsonOptions));
        return result with { Finding = resolution };
    }

    internal static FuzzFindingResolution Resolve(string target, string failureId, string path)
    {
        var fingerprint = Normalize(failureId);
        var finding = Load(path).SingleOrDefault(item =>
            string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Normalize(item.Fingerprint), fingerprint, StringComparison.Ordinal));
        return finding == null
            ? new FuzzFindingResolution(target, failureId, fingerprint, FuzzFindingStatus.Unknown, null)
            : new FuzzFindingResolution(target, failureId, fingerprint, finding.Status, finding.Issue);
    }

    internal static FuzzFindingResolution Resolve(string target, string failureId) =>
        Resolve(target, failureId, DefaultPath());

    internal static bool ShouldMinimize(bool discoveryRun, FuzzFindingResolution finding) =>
        !discoveryRun || !finding.AllowsDiscoveryToContinue;

    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "UNKNOWN_FUZZ_FAILURE";
        var normalized = GuidPattern().Replace(value.ToUpperInvariant(), "_VALUE_");
        normalized = PageAddressPattern().Replace(normalized, "_PAGE_");
        normalized = VolatileValuePattern().Replace(normalized, match => $"_{match.Groups[1].Value}_VALUE_");
        normalized = HexValuePattern().Replace(normalized, "_VALUE_");
        normalized = UnsafeCharacterPattern().Replace(normalized, "_");
        return RepeatedSeparatorPattern().Replace(normalized, "_").Trim('_');
    }

    private static IReadOnlyList<FuzzFinding> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<FuzzFinding>();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException($"Unsupported fuzz finding registry schema in {path}.");

        var findings = new List<FuzzFinding>();
        foreach (var element in root.GetProperty("findings").EnumerateArray())
        {
            var statusText = element.GetProperty("status").GetString();
            if (!Enum.TryParse<FuzzFindingStatus>(statusText, true, out var status) ||
                status == FuzzFindingStatus.Unknown)
            {
                throw new InvalidDataException($"Unknown fuzz finding status '{statusText}' in {path}.");
            }
            findings.Add(new FuzzFinding(
                element.GetProperty("target").GetString(),
                element.GetProperty("fingerprint").GetString(),
                element.GetProperty("issue").GetInt32(),
                status));
        }
        return findings;
    }

    private static string ReadFailureId(string directory)
    {
        var determinism = Path.Combine(directory, "determinism-failure.json");
        if (File.Exists(determinism))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(determinism));
            return document.RootElement.GetProperty("failureId").GetString();
        }
        var replay = Path.Combine(directory, "replay.json");
        if (File.Exists(replay)) return FuzzArtifacts.ReadReplay(replay).FailureId ?? "UNKNOWN_FUZZ_FAILURE";
        var run = Path.Combine(directory, "run.json");
        if (!File.Exists(run)) return "UNKNOWN_FUZZ_FAILURE";
        using var runDocument = JsonDocument.Parse(File.ReadAllText(run));
        return runDocument.RootElement.TryGetProperty("failureId", out var failureId)
            ? failureId.GetString() ?? "UNKNOWN_FUZZ_FAILURE"
            : "UNKNOWN_FUZZ_FAILURE";
    }

    private static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, "Corpus", "known-findings.json");

    [GeneratedRegex(@"(?<![0-9A-F])[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}(?![0-9A-F])")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"(?<![0-9A-F])[0-9A-F]{2,8}:[0-9A-F]{2,8}(?![0-9A-F])")]
    private static partial Regex PageAddressPattern();

    [GeneratedRegex(@"(?<![A-Z0-9])(SEED|STEP|PAGE|ADDRESS|ORDINAL|DOCUMENT_ID|DOCUMENTID|DOC_ID|DOCID|COUNT|RANDOM)\s*[:=#_-]?\s*(?:0X)?[0-9A-F]+\b")]
    private static partial Regex VolatileValuePattern();

    [GeneratedRegex(@"(?<![A-Z0-9])0X[0-9A-F]+(?![A-Z0-9])")]
    private static partial Regex HexValuePattern();

    [GeneratedRegex(@"[^A-Z0-9]+")]
    private static partial Regex UnsafeCharacterPattern();

    [GeneratedRegex(@"_+")]
    private static partial Regex RepeatedSeparatorPattern();
}
