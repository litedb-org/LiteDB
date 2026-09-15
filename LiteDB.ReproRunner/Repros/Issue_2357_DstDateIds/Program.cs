using System.Diagnostics;

using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "child")
        {
            return DateIdScenario.Run(args[1], args[2]);
        }

        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2357-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RequireControl(RunChild(directory, "unspecified", "Etc/UTC"));
            RequireControl(RunChild(directory, "utc", "America/New_York"));
            var target = RunChild(directory, "unspecified", "America/New_York");
            if (target.ExitCode == 0 && target.Output.Contains("DUPLICATE_DATE_2357"))
            {
                Console.WriteLine("BUG_2357_CONFIRMED: 480 unique wall-clock IDs collide at the 2006 DST transition; UTC controls and rollback ledger passed");
                return 0;
            }
            if (target.ExitCode == 10 && target.Output.Contains("VERIFIED_DATE_IDS_2357"))
            {
                Console.WriteLine("VERIFIED_2357: wall-clock IDs were preserved or invalid local times were explicitly rejected without losing committed data");
                return 10;
            }
            throw new InvalidOperationException("The target did not satisfy an exact outcome contract.");
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 20;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RequireControl(ChildResult result)
    {
        if (result.ExitCode != 10 || !result.Output.Contains("VERIFIED_DATE_IDS_2357"))
        {
            throw new InvalidOperationException("A timezone/kind control failed.");
        }
    }

    private static ChildResult RunChild(string directory, string kind, string zone)
    {
        var path = Path.Combine(directory, kind + "-" + zone.Replace('/', '-') + ".db");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("child");
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(path);
        start.Environment["TZ"] = zone;
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "0";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start timezone probe.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20_000))
        {
            child.Kill(true);
            if (!child.WaitForExit(5_000))
            {
                throw new TimeoutException("Timezone probe did not terminate.");
            }
            throw new TimeoutException("Timezone probe exceeded 20 seconds.");
        }
        if (!Task.WaitAll(new Task[] { stdout, stderr }, 5_000))
        {
            throw new TimeoutException("Timezone probe output did not close.");
        }
        var output = stdout.Result + stderr.Result;
        Console.Write(output);
        return new ChildResult(child.ExitCode, output);
    }

    private sealed record ChildResult(int ExitCode, string Output);
}
