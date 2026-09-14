using System.Diagnostics;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private const string MonoImage =
        "mono:6.12.0.182@sha256:34d816779b1248b5cfd095770b64ecbaf1798e2aca693a91c11a018dce9c7ad5";

    private static int Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        try
        {
            var start = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // The compiler and database write only to the disposable container's
            // /tmp. Runtime networking is disabled and source inputs are read-only.
            foreach (var argument in new[]
            {
                "run", "--rm", "--network", "none", "--user", "1000:1000",
                "--mount", "type=bind,source=" + AppContext.BaseDirectory + ",target=/repro,readonly",
                "--workdir", "/tmp", MonoImage, "timeout", "180s", "sh", "-c",
                "mcs -langversion:7.2 -out:/tmp/Issue1472.exe " +
                "-r:/repro/mono-input/LiteDB.dll -r:/usr/lib/mono/4.7.2-api/Facades/netstandard.dll " +
                "/repro/MonoProgram.cs /repro/Issue1472_CompoundQueryScenario.cs && " +
                "MONO_PATH=/repro/mono-input mono /tmp/Issue1472.exe"
            })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(210_000))
            {
                process.Kill(entireProcessTree: true);
                Console.Error.WriteLine("Mono comparison exceeded its timeout.");
                return 20;
            }
            var output = stdout.GetAwaiter().GetResult();
            Console.Write(output);
            Console.Error.Write(stderr.GetAwaiter().GetResult());
            if (process.ExitCode == 0 && output.Contains("BUG_1472_CONFIRMED")) return 0;
            if (process.ExitCode == 10 && output.Contains("VERIFIED_1472")) return 10;
            Console.Error.WriteLine("Mono comparison returned an unclassified result: " + process.ExitCode);
            return 20;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 20;
        }
    }
}
