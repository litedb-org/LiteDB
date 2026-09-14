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
                new Issue2169_CheckpointFailureTests().Parallel_inserts_preserve_the_checkpoint_error_and_every_committed_payload();
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
        Console.WriteLine($"REPEATS_2169: attempts={attempts}, reportedFailures={failures}");
        Console.WriteLine(failures > 0 ? "BUG_2169_CONFIRMED" : "VERIFIED_2169");
        return failures > 0 ? 0 : 10;
    }

    private static bool IsReportedFailure(Exception error)
    {
        return error is Xunit.Sdk.XunitException && error.Message.Contains("but found ") && error.Message.Contains("transaction must be active to rollback") && error.Message.Contains("checkpoint access denied");
    }
}
