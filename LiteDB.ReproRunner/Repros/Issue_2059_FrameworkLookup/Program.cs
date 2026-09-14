using System.Diagnostics;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static async Task<int> Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var windows = OperatingSystem.IsWindows();
                var assembly = Path.Combine(AppContext.BaseDirectory, "worker", "Issue2059.Worker" + (windows ? ".exe" : ".dll"));
                var start = new ProcessStartInfo(windows ? assembly : "dotnet")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (!windows) start.ArgumentList.Add(assembly);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
                var output = child.StandardOutput.ReadToEndAsync();
                var errors = child.StandardError.ReadToEndAsync();
                try
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180));
                }
                catch
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                    Console.Write(await output);
                    Console.Error.Write(await errors);
                    throw;
                }
                var transcript = await output;
                Console.Write(transcript);
                Console.Error.Write(await errors);
                var runtime = windows ? ".NETFramework,Version=v4.8, bits=32" : ".NETCoreApp,Version=v8.0";
                if (child.ExitCode != 0 || !transcript.Contains("VERIFIED_WORKER_2059:") ||
                    !transcript.Contains("RUNTIME_2059: target=" + runtime))
                    throw new InvalidOperationException($"Attempt {attempt} failed or used the wrong runtime: exit {child.ExitCode}.");
                Console.WriteLine($"ATTEMPT_2059_{attempt}: fresh-process ledger and backup-lock controls passed");
            }
            Console.WriteLine("VERIFIED_2059: attempts=3; originating invalid-segment failure not reproduced");
            return 10;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 20;
        }
    }
}
