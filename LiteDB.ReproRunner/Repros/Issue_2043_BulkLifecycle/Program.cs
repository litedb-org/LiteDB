using System.Diagnostics;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;
using LiteDB.Tests.Issues;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--child")
        {
            try
            {
                Issue2043_BulkLifecycleTests.Exercise(1792, 32768);
                Console.WriteLine("CHILD_2043_VERIFIED");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 20;
            }
        }

        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--child");
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(180000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                Console.Error.WriteLine($"ATTEMPT_2043_{attempt}: timed out");
                return 20;
            }
            Console.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(errors.GetAwaiter().GetResult());
            if (child.ExitCode != 0 || !output.Result.Contains("CHILD_2043_VERIFIED")) return 20;
            Console.WriteLine($"ATTEMPT_2043_{attempt}: verified in fresh process");
        }
        Console.WriteLine("VERIFIED_2043: 3 fresh processes; reported duplicate-auto-ID failure not reproduced");
        return 10;
    }
}
