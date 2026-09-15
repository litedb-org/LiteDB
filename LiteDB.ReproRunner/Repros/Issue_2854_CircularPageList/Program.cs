using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--child")
        {
            return Issue2854Scenario.RunChild(args[1], args[2]);
        }

        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var context = ReproContext.FromEnvironment();
        var root = string.IsNullOrWhiteSpace(context.SharedDatabaseRoot)
            ? Path.Combine(Path.GetTempPath(), "litedb-2854-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(context.SharedDatabaseRoot, "fixture");

        Directory.CreateDirectory(root);
        try
        {
            return RunParent(root);
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 20;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static int RunParent(string root)
    {
        var pristine = Path.Combine(root, "pristine.db");
        var healthyDrop = Path.Combine(root, "healthy-drop.db");
        var healthyWalk = Path.Combine(root, "healthy-page-list.db");
        var corruptDrop = Path.Combine(root, "corrupt-drop.db");
        var corruptWalk = Path.Combine(root, "corrupt-page-list.db");

        Issue2854Scenario.CreatePristineDatabase(pristine);
        var layout = RawPageListFixture.InspectHealthy(pristine);
        File.Copy(pristine, healthyDrop, true);
        File.Copy(pristine, healthyWalk, true);
        RawPageListFixture.CreateCycle(pristine, corruptDrop, layout);
        RawPageListFixture.CreateCycle(pristine, corruptWalk, layout);

        Console.WriteLine(
            $"FIXTURE_2854_PROVED collectionPage={layout.CollectionPageId}, slot={layout.Slot}, " +
            $"cyclePages={layout.Chain.Count}, dataPages={layout.DataPageIds.Count}");

        RequireHealthyControl(
            Issue2854Scenario.HealthyDropMarker,
            ChildProbeRunner.Run(typeof(Program).Assembly.Location, "drop-healthy", healthyDrop));

        var healthyWalkHash = RawPageListFixture.Hash(healthyWalk);
        RequireHealthyControl(
            Issue2854Scenario.HealthyPageListMarker,
            ChildProbeRunner.Run(typeof(Program).Assembly.Location, "page-list-healthy", healthyWalk));
        RequireUnchangedHealthyPageList(healthyWalk, healthyWalkHash);

        var dropStructuralHash = RawPageListFixture.StructuralHash(corruptDrop);
        var drop = ClassifyDangerousResult(
            Issue2854Scenario.DropOperation,
            corruptDrop,
            layout,
            ChildProbeRunner.Run(typeof(Program).Assembly.Location, "drop-corrupt", corruptDrop));
        if (drop.Kind == ResultKind.Safe)
        {
            RequireCorruptionSignal(
                Issue2854Scenario.DropOperation, corruptDrop, dropStructuralHash, layout);
        }

        var walkHash = RawPageListFixture.Hash(corruptWalk);
        var walkStructuralHash = RawPageListFixture.StructuralHash(corruptWalk);
        var walk = ClassifyDangerousResult(
            Issue2854Scenario.PageListOperation,
            corruptWalk,
            layout,
            ChildProbeRunner.Run(typeof(Program).Assembly.Location, "page-list-corrupt", corruptWalk));
        if (walk.Kind == ResultKind.Safe)
        {
            RequireCorruptionSignal(
                Issue2854Scenario.PageListOperation, corruptWalk, walkStructuralHash, layout);
        }
        else if (walk.Kind == ResultKind.Bug)
        {
            RequireUnchangedCorruption(Issue2854Scenario.PageListOperation, corruptWalk, walkHash, layout);
        }

        if (drop.Kind == ResultKind.HarnessFailure || walk.Kind == ResultKind.HarnessFailure)
        {
            throw new InvalidOperationException(
                $"child probe failed unexpectedly: drop={drop.Detail}; page-list={walk.Detail}");
        }

        if (drop.Kind == ResultKind.Bug || walk.Kind == ResultKind.Bug)
        {
            Console.WriteLine(
                $"BUG_2854_CONFIRMED: drop={drop.Detail}; page-list={walk.Detail}; " +
                "a reachable circular page list was not rejected promptly with LiteException error 999");
            return 0;
        }

        Console.WriteLine(
            "VERIFIED_2854: independent healthy controls completed and both corrupt page-list walks " +
            "promptly raised LiteException error 999 with only the recovery marker changed");
        return 10;
    }

    private static void RequireHealthyControl(string marker, ChildProbeResult result)
    {
        if (result.TimedOut || !result.OutputComplete || result.ExitCode != 0 ||
            !result.StandardOutput.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"healthy control {marker} failed: {result.Describe()}");
        }
    }

    private static ClassifiedResult ClassifyDangerousResult(
        string operation,
        string path,
        HealthyLayout layout,
        ChildProbeResult result)
    {
        if (result.TimedOut)
        {
            RawPageListFixture.AssertExactCycle(path, layout);
            return new ClassifiedResult(
                ResultKind.Bug,
                $"{operation} exceeded the {ChildProbeRunner.ExecutionTimeoutSeconds}-second deadline " +
                $"({result.ShutdownDetail})");
        }

        var expectedMarker = Issue2854Scenario.CorruptionMarker + ": " + operation + ":";
        if (result.OutputComplete &&
            result.ExitCode == Issue2854Scenario.ExpectedCorruptionExitCode &&
            result.StandardOutput.Contains(expectedMarker, StringComparison.Ordinal))
        {
            return new ClassifiedResult(ResultKind.Safe, operation + " raised error 999");
        }

        if (result.OutputComplete &&
            result.ExitCode == Issue2854Scenario.UnsafeCompletionExitCode &&
            (result.StandardOutput.Contains("UNSAFE_COMPLETION_2854", StringComparison.Ordinal) ||
             result.StandardOutput.Contains("UNSAFE_EXCEPTION_2854", StringComparison.Ordinal)))
        {
            return new ClassifiedResult(ResultKind.Bug, operation + " completed without a corruption error");
        }

        return new ClassifiedResult(ResultKind.HarnessFailure, result.Describe(operation));
    }

    private static void RequireUnchangedHealthyPageList(string path, string expectedHash)
    {
        if (RawPageListFixture.Hash(path) != expectedHash)
        {
            throw new InvalidOperationException("healthy $page_list control changed its database");
        }

        RawPageListFixture.InspectHealthy(path);
    }

    private static void RequireCorruptionSignal(
        string operation,
        string path,
        string expectedStructuralHash,
        HealthyLayout layout)
    {
        RawPageListFixture.AssertRecoveryMarker(path);
        if (RawPageListFixture.StructuralHash(path) != expectedStructuralHash)
        {
            throw new InvalidOperationException(
                operation + " changed bytes other than LiteDB's recovery marker");
        }

        RawPageListFixture.AssertExactCycle(path, layout);
    }

    private static void RequireUnchangedCorruption(
        string operation,
        string path,
        string expectedHash,
        HealthyLayout layout)
    {
        if (RawPageListFixture.Hash(path) != expectedHash)
        {
            throw new InvalidOperationException(operation + " changed the corrupted fixture without error 999");
        }

        RawPageListFixture.AssertExactCycle(path, layout);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // A failed cleanup must not hide the regression outcome.
        }
    }

    private sealed record ClassifiedResult(ResultKind Kind, string Detail);
    private enum ResultKind { Safe, Bug, HarnessFailure }
}
