using System;
using System.IO;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2043_BulkLifecycleTests
    {
        [Fact]
        public void Bulk_auto_ids_survive_timer_cleanup_ordered_readers_and_quiesced_rebuilds()
        {
            Exercise(128, 1024);
        }

        internal static void Exercise(int seedCount, int payloadBytes)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2043-bulk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "records.db");
                var storagePath = Path.Combine(directory, "files.db");
                Issue2043_BulkLifecycleScenario scenario;
                using (var records = new LiteDatabase(path))
                using (var files = new LiteDatabase(storagePath))
                {
                    scenario = new Issue2043_BulkLifecycleScenario(records, files, seedCount, payloadBytes);
                    scenario.Run(path);
                }
                scenario.VerifyReopened(path, storagePath);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
