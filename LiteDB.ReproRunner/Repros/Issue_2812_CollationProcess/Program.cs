using System;
using System.Diagnostics;
using System.IO;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return args.Length == 2 ? RunChild(args[0], args[1]) : 20;
        }

        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2812-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var reproduced = false;

            foreach (var writerInvariant in new[] { false, true })
            {
                var label = writerInvariant ? "invariant" : "host";
                var path = Path.Combine(directory, label + ".db");

                RequireExit("write", RunProcess("write", path, writerInvariant), 10);
                RequireExit("same-environment read", RunProcess("read", path, writerInvariant), 10);

                reproduced |= AcceptCrossResult("read", RunProcess("cross-read", path, !writerInvariant));

                var sameUpsertPath = Path.Combine(directory, label + "-same-upsert.db");
                CopyFixture(path, sameUpsertPath);
                RequireExit("same-environment upsert", RunProcess("upsert", sameUpsertPath, writerInvariant), 10);

                var crossUpsertPath = Path.Combine(directory, label + "-cross-upsert.db");
                CopyFixture(path, crossUpsertPath);
                reproduced |= AcceptCrossResult("upsert", RunProcess("cross-upsert", crossUpsertPath, !writerInvariant));
            }

            if (reproduced)
            {
                Console.WriteLine("BUG_2812_CONFIRMED: a changed globalization comparer silently loses indexed keys or unique-key updates");
                return 0;
            }

            Console.WriteLine("VERIFIED_2812: comparer changes preserved reads and upserts or rejected the file with rebuild guidance");
            return 10;
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

    private static int RunChild(string mode, string path)
    {
        try
        {
            var fingerprint = Issue2812Fixture.GetRuntimeSortFingerprint();

            if (mode == "write")
            {
                Issue2812Fixture.Write(path, fingerprint);
                return 10;
            }

            var writerFingerprint = File.ReadAllText(path + ".sort-order");
            var sameEnvironment = mode == "read" || mode == "upsert";
            if (sameEnvironment != string.Equals(writerFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(sameEnvironment
                    ? "same-environment control used a different runtime sort order"
                    : "cross-environment precondition failed: runtime sort orders are identical");
            }

            return mode switch
            {
                "read" => Issue2812Fixture.Read(path, false, writerFingerprint),
                "cross-read" => Issue2812Fixture.Read(path, true, writerFingerprint),
                "upsert" => Issue2812Fixture.Upsert(path, false),
                "cross-upsert" => Issue2812Fixture.Upsert(path, true),
                _ => throw new InvalidOperationException("unknown child mode: " + mode)
            };
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 20;
        }
    }

    private static bool AcceptCrossResult(string operation, int result)
    {
        if (result == 0)
        {
            return true;
        }

        RequireExit("cross-environment " + operation, result, 10);
        return false;
    }

    private static void CopyFixture(string source, string destination)
    {
        File.Copy(source, destination);
        File.Copy(source + ".sort-order", destination + ".sort-order");
    }

    private static int RunProcess(string mode, string path, bool invariant)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(path);
        startInfo.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = invariant ? "1" : "0";
        startInfo.Environment["DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY"] = "0";

        using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start globalization probe");
        if (!child.WaitForExit(15_000))
        {
            child.Kill(true);
            child.WaitForExit();
            throw new TimeoutException($"globalization probe timed out in mode {mode}");
        }

        return child.ExitCode;
    }

    private static void RequireExit(string operation, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"{operation} exited {actual}; expected {expected}");
        }
    }
}
