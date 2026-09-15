using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return args.Length == 3 && args[0] == "--child"
                ? UpgradeScenario.RunChild(args[1], args[2])
                : UpgradeScenario.HarnessFailureExitCode;
        }

        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var context = ReproContext.FromEnvironment();
        var root = string.IsNullOrWhiteSpace(context.SharedDatabaseRoot)
            ? Path.Combine(Path.GetTempPath(), "litedb-2826-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(context.SharedDatabaseRoot, "issue-2826-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            return RunParent(root);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return UpgradeScenario.HarnessFailureExitCode;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static int RunParent(string root)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "v4.1.4-pristine.db");
        var healthyPath = Path.Combine(root, "healthy.db");
        var selfLoopPath = Path.Combine(root, "self-loop.db");
        var pastEofPath = Path.Combine(root, "past-eof.db");
        var layout = V7FixtureInspector.InspectPristine(source);

        File.Copy(source, healthyPath);
        V7FixtureMutator.CreateSelfLoop(source, selfLoopPath, layout);
        V7FixtureMutator.CreatePastEof(source, pastEofPath, layout);
        _ = V7FixtureInspector.InspectPristine(healthyPath);

        Console.WriteLine(
            $"FIXTURE_2826_PROVED: sha256={layout.SourceSha256}, pages={layout.PageCount}, " +
            $"firstExtend={layout.FirstExtendPageId}, originalNext={layout.OriginalNextPageId}, " +
            $"selfLoopNext={layout.FirstExtendPageId}, pastEofNext={layout.PageCount + 1}");

        var assembly = typeof(Program).Assembly.Location;
        RequireHealthy(BoundedUpgradeProbe.Run(assembly, UpgradeScenario.HealthyMode, healthyPath));
        var selfLoop = ClassifyCorrupt(
            UpgradeScenario.SelfLoopMode,
            BoundedUpgradeProbe.Run(assembly, UpgradeScenario.SelfLoopMode, selfLoopPath));
        var pastEof = ClassifyCorrupt(
            UpgradeScenario.PastEofMode,
            BoundedUpgradeProbe.Run(assembly, UpgradeScenario.PastEofMode, pastEofPath));

        if (selfLoop.Kind == CorruptResultKind.HarnessFailure || pastEof.Kind == CorruptResultKind.HarnessFailure)
        {
            throw new InvalidOperationException(
                $"corrupt upgrade probe failed: self-loop={selfLoop.Detail}; past-eof={pastEof.Detail}");
        }

        if (selfLoop.Kind == CorruptResultKind.Bug || pastEof.Kind == CorruptResultKind.Bug)
        {
            Console.WriteLine($"BUG_2826_CONFIRMED: self-loop={selfLoop.Detail}; past-eof={pastEof.Detail}");
            return 0;
        }

        Console.WriteLine(
            $"VERIFIED_2826: self-loop={selfLoop.Detail}; past-eof={pastEof.Detail}; " +
            "both corrupt upgrades failed gracefully or retained the intact document with an error ledger");
        return UpgradeScenario.FixedExitCode;
    }

    private static void RequireHealthy(UpgradeProbeResult result)
    {
        if (result.StopReason != null || !result.OutputComplete || result.ExitCode != 0 ||
            !result.StandardOutput.Contains(UpgradeScenario.HealthyMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("healthy upgrade control failed: " + result.Describe(UpgradeScenario.HealthyMode));
        }

        Console.WriteLine(
            $"HEALTHY_CONTROL_2826_PROVED: peakMiB={result.PeakWorkingSetBytes / 1024d / 1024d:F1}, " +
            "both exact source documents persisted after reopen");
    }

    private static ClassifiedCorruptResult ClassifyCorrupt(string mode, UpgradeProbeResult result)
    {
        if (result.StopReason != null)
        {
            if (!result.Started || !result.OutputComplete || !result.Terminated || result.ExitCode == null)
            {
                return Failure(result.Describe(mode));
            }

            var kind = result.StopReason == ProbeStopReason.Timeout ? "loop timeout" : "memory-limit breach";
            return Bug($"{kind} after start (peak {result.PeakWorkingSetBytes / 1024d / 1024d:F1} MiB)");
        }

        if (!result.OutputComplete || !result.Started)
        {
            return Failure(result.Describe(mode));
        }

        if (result.ExitCode == UpgradeScenario.KnownBugExitCode &&
            result.StandardOutput.Contains(UpgradeScenario.KnownBugMarker, StringComparison.Ordinal))
        {
            return Bug(ExtractLastMarkerLine(result.StandardOutput, UpgradeScenario.KnownBugMarker));
        }

        if (result.ExitCode == UpgradeScenario.FixedExitCode &&
            (result.StandardOutput.Contains(UpgradeScenario.FixedExceptionMarker, StringComparison.Ordinal) ||
             result.StandardOutput.Contains(UpgradeScenario.FixedErrorsMarker, StringComparison.Ordinal)))
        {
            return Fixed(ExtractLastMarkerLine(result.StandardOutput, "FIXED_"));
        }

        var combined = result.StandardOutput + "\n" + result.StandardError;
        if (result.ExitCode != 0 &&
            combined.Contains("Out of memory", StringComparison.OrdinalIgnoreCase))
        {
            return Bug("child runtime reported Out of memory after entering the corrupt upgrade");
        }

        return Failure(result.Describe(mode));
    }

    private static string ExtractLastMarkerLine(string output, string marker) =>
        output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.Contains(marker, StringComparison.Ordinal));

    private static ClassifiedCorruptResult Bug(string detail) => new(CorruptResultKind.Bug, detail);
    private static ClassifiedCorruptResult Fixed(string detail) => new(CorruptResultKind.Fixed, detail);
    private static ClassifiedCorruptResult Failure(string detail) => new(CorruptResultKind.HarnessFailure, detail);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private sealed record ClassifiedCorruptResult(CorruptResultKind Kind, string Detail);
    private enum CorruptResultKind { Bug, Fixed, HarnessFailure }
}
