using System.Diagnostics;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace SharedSelfTeardownProof;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--child")
        {
            try { return Scenario.Run(args[1], args[2] == "encrypted", args[3]); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var directory = Fixture.CanonicalDirectory("self-teardown-");
        host.SendLog("Original fixtures and child diagnostics retained at " + directory);
        try
        {
            if (args.Length != 1 || args[0] != "--release-only" && args[0] != "--wait-only") throw new ArgumentException("Select exactly one proof mode.");
            var modes = args[0] == "--release-only" ? new[] { "dispose" } : new[] { "getter" };
            var outcomes = new List<int>();
            foreach (var encrypted in new[] { false, true })
            {
                if (Child("control", encrypted, directory) != 2) throw new Exception("Positive control failed.");
                foreach (var mode in modes) outcomes.Add(Child(mode, encrypted, directory));
            }
            if (outcomes.Distinct().Count() != 1) throw new Exception("Mixed defect/fixed outcomes.");
            var reproduced = outcomes[0] == 0;
            var message = reproduced ? "TEARDOWN_DEFECT_VERIFIED: independent native ownership/dependency witnesses."
                : "FIXED_VERIFIED: exact refusals, retained ownership, positive controls and cold state verified.";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Unexpected proof failure.", new { Exception = error.ToString(), Directory = directory });
            return 1;
        }
    }

    private static int Child(string mode, bool encrypted, string directory)
    {
        var name = mode + (encrypted ? "-encrypted" : "-plain");
        var childDirectory = Path.Combine(directory, name);
        Directory.CreateDirectory(childDirectory);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--child", mode, encrypted ? "encrypted" : "plain", childDirectory }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Child did not start.");
        // Drain both pipes concurrently and preserve every line even if the watchdog kills
        // a genuinely unexpected hang. A deadline never counts as a known-bad witness.
        var output = Capture(process.StandardOutput, Path.Combine(childDirectory, "stdout.log"));
        var error = Capture(process.StandardError, Path.Combine(childDirectory, "stderr.log"));
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WaitAll(output, error);
            throw new Exception(name + ": hard watchdog expired; fixture retained; this is NOT a successful reproduction.");
        }
        Task.WaitAll(output, error);
        var text = output.Result;
        Console.WriteLine(name + ": exit=" + process.ExitCode + "\n" + text);
        if (error.Result.Length != 0 || process.ExitCode != 0 && process.ExitCode != 2)
            throw new Exception(name + ": unexpected child result. " + error.Result);
        var required = process.ExitCode == 2 ? "CHILD_FIXED_VERIFIED" : mode == "getter" ? "SELF_WAIT_VERIFIED" : "EARLY_RELEASE_VERIFIED";
        if (!text.Contains(required, StringComparison.Ordinal)) throw new Exception(name + ": missing semantic witness " + required);
        return process.ExitCode;
    }

    private static async Task<string> Capture(StreamReader reader, string path)
    {
        using var writer = new StreamWriter(path) { AutoFlush = true };
        var lines = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
            lines.AppendLine(line);
        }
        return lines.ToString();
    }
}
