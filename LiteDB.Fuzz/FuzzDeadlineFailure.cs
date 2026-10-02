using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LiteDB.Tests.Safety;

namespace LiteDB.Fuzz;

/// <summary>
/// The overdue path of the deadline oracle. It runs on the watchdog thread while the overdue
/// operation is still blocked on its own thread: it records the failure (stable id
/// <c>DEADLINE_&lt;TARGET&gt;_&lt;OP&gt;</c>, the operation, dimension, elapsed time, every operation
/// in flight on every thread, and managed stacks when <c>dotnet-dump</c> can provide them) into
/// <c>deadline-failure.json</c>, hands it to the run's failure handler (the same path as any
/// other failure: run.json, replay.json, minimization), and terminates the process.
/// </summary>
internal static class FuzzDeadlineFailure
{
    /// <summary>Process exit code of a run terminated by an overdue operation.</summary>
    internal const int ExitCode = 3;

    internal static string FailureId(string target, string operation) =>
        $"DEADLINE_{FuzzOracles.Safe(target)}_{FuzzOracles.Safe(operation)}";

    internal static void Report(FuzzContext context, FuzzOracleState state, InFlightOperation overdue,
        InFlightOperation[] inFlight)
    {
        var failureId = FailureId(context.Target, overdue.Operation);
        var message = $"Operation {overdue.Operation} ({overdue.Dimension}) did not complete or throw within its declared " +
            $"{overdue.Deadline.TotalSeconds:F1} s deadline at step {overdue.Step}; {inFlight.Length} operation(s) in flight.";
        state.WriteOutcome(overdue.Operation, overdue.Dimension, overdue.Step, "hang", null, overdue.ElapsedMs);
        var path = Path.Combine(context.DirectoryPath, FuzzOracleState.DeadlineFailureFile);
        File.WriteAllText(path, Serialize(context, failureId, message, overdue, inFlight, "pending"));
        context.PulseHeartbeat();
        var stacks = CaptureStacks(context);
        File.WriteAllText(path, Serialize(context, failureId, message, overdue, inFlight, stacks));
        var handler = context.DeadlineFailureHandler;
        if (handler == null) return;
        try { handler(new FuzzFailureException(failureId, message)).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(context.DirectoryPath, "deadline-handler-error.txt"), error.ToString());
        }
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(ExitCode);
    }

    private static string Serialize(FuzzContext context, string failureId, string message, InFlightOperation overdue,
        InFlightOperation[] inFlight, string stacks) => System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, failureId, message, target = context.Target, seed = context.Seed,
            step = overdue.Step, op = overdue.Operation, dimension = overdue.Dimension,
            elapsedMs = Math.Round(overdue.ElapsedMs, 1), deadlineMs = overdue.Deadline.TotalMilliseconds,
            deadlineRule = "declared by the scenario (lock-bound default max(3 x TIMEOUT pragma, 15 s)), kept 5 s below the runner hang timeout",
            inFlight = inFlight.Select(item => new
            {
                item.Operation, item.Dimension, item.Step, item.ManagedThreadId, item.ThreadName,
                elapsedMs = Math.Round(item.ElapsedMs, 1), deadlineMs = item.Deadline.TotalMilliseconds
            }),
            stacks
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Managed stacks of every thread. .NET cannot read another thread's stack in-process, so
    /// this asks <c>dotnet-dump</c> (the repository's tool manifest) for a dump of this process and
    /// prints <c>clrstack -all</c>. Best effort and bounded; the dump itself is not kept.
    /// </summary>
    private static string CaptureStacks(FuzzContext context)
    {
        var dump = Path.Combine(Path.GetTempPath(), $"litedb-deadline-{Environment.ProcessId}.dmp");
        try
        {
            var collect = Run(context, TimeSpan.FromSeconds(30), "tool", "run", "dotnet-dump", "collect",
                "--process-id", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--type", "Heap", "--output", dump);
            if (!File.Exists(dump)) return "unavailable: dotnet-dump collect failed: " + collect;
            var stacks = Run(context, TimeSpan.FromSeconds(60), "tool", "run", "dotnet-dump", "analyze", dump,
                "--command", "clrstack -all", "--command", "exit");
            return string.IsNullOrWhiteSpace(stacks) ? "unavailable: dotnet-dump analyze printed nothing" : stacks;
        }
        catch (Exception error) { return "unavailable: " + error.Message; }
        finally
        {
            try { File.Delete(dump); }
            catch (IOException) { }
        }
    }

    /// <summary>The checkout whose tool manifest provides dotnet-dump: the nearest ancestor of the binaries.</summary>
    private static string ToolManifestRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ".config", "dotnet-tools.json"))) return directory.FullName;
        return Environment.CurrentDirectory;
    }

    private static string Run(FuzzContext context, TimeSpan timeout, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = ToolManifestRoot()
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow + timeout;
        while (!process.WaitForExit(500))
        {
            context.PulseHeartbeat();
            if (DateTime.UtcNow < deadline) continue;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return "timed out";
        }
        return output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
    }
}
