using System.IO;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;
using LiteDB.Tests.Issues;

internal static class Program
{
    private static int Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        if (!int.TryParse(Environment.GetEnvironmentVariable("LITEDB_REPRO_ATTEMPTS") ?? "3", out var attempts) || attempts < 1 || attempts > 1000)
        {
            Console.Error.WriteLine("LITEDB_REPRO_ATTEMPTS must be between 1 and 1000.");
            return 20;
        }
        var failures = 0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                new Issue2816_DisposeFailureTests().Log_metadata_failure_during_dispose_still_releases_the_owned_data_file();
                Console.WriteLine($"ATTEMPT_{attempt}: all regression and persistence assertions passed");
            }
            catch (Exception error) when (IsReportedFailure(error))
            {
                failures++;
                Console.WriteLine($"ATTEMPT_{attempt}: {error.Message}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 20;
            }
        }
        Console.WriteLine($"REPEATS_2816: attempts={attempts}, reportedFailures={failures}");
        Console.WriteLine(failures > 0 ? "BUG_2816_CONFIRMED" : "VERIFIED_2816");
        return failures > 0 ? 0 : 10;
    }

    private static bool IsReportedFailure(Exception error)
    {
        return error is Xunit.Sdk.XunitException &&
            error.Message.Contains("Dispose must release its owned data-file handle before any finalization") &&
            error.Message.Contains("being used by another process");
    }
}
