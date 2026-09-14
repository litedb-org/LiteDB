using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;
using LiteDB.Tests.Issues;
using Issue_2825_FreeListRace;

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
                new Issue2825_Tests().Parallel_collection_page_reuse_preserves_every_surviving_payload_after_reopen();
                Console.WriteLine($"ATTEMPT_{attempt}: all regression and persistence assertions passed");
            }
            catch (Exception error) when (FreeListFailureClassifier.IsReportedFailure(error))
            {
                failures++;
                Console.WriteLine($"ATTEMPT_{attempt}: {error}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 20;
            }
        }
        Console.WriteLine($"REPEATS_2825: attempts=3, reportedFailures={failures}");
        Console.WriteLine(failures > 0 ? "BUG_2825_CONFIRMED" : "VERIFIED_2825");
        return failures > 0 ? 0 : 10;
    }

}
