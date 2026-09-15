using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;
using LiteDB.Tests.Issues;

internal static class Program
{
    private static int Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var failures = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                new Issue2842_Tests().Size_limit_is_classified_and_failed_write_does_not_change_committed_rows();
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
        Console.WriteLine($"REPEATS_2842: attempts=3, reportedFailures={failures}");
        Console.WriteLine(failures > 0 ? "BUG_2842_CONFIRMED" : "VERIFIED_2842");
        return failures > 0 ? 0 : 10;
    }

    private static bool IsReportedFailure(Exception error)
    {
        return error is Xunit.Sdk.XunitException && error.Message.Trim() == "Expected lite.ErrorCode to be 105, but found 0 (difference of -105).";
    }
}
