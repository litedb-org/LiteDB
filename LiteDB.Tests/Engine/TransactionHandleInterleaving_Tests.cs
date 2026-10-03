using System;
using System.Collections.Generic;
using System.IO;
using LiteDB.ConcurrencyTesting;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleInterleaving_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            // The PR subset runs every schedule in both modes once, alternating encryption and
            // outcome; LITEDB_EXPLORER_FULL=1 runs the whole Cartesian matrix.
            var full = Environment.GetEnvironmentVariable("LITEDB_EXPLORER_FULL") == "1";
            foreach (var shared in new[] { false })
                for (var schedule = 0; schedule < TransactionInterleavingExplorer.ScheduleCount(shared); schedule++)
                    foreach (var encrypted in new[] { false, true })
                        foreach (var outcome in new[] { 0, 1 })
                            if (full || (encrypted == ((schedule & 2) != 0) && outcome == (schedule & 1)))
                                yield return new object[] { shared, encrypted, schedule, outcome };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Forced_concurrent_histories_preserve_transaction_contracts(bool shared, bool encrypted, int schedule, int seed)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-interleavings-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(directory, "state.db");
            // Failure paths deliberately retain originals, WAL and flushed actor history.
            TransactionInterleavingExplorer.Run(file, shared, encrypted, schedule, seed);
            Directory.Delete(directory, true);
        }
    }
}
