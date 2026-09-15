using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using FluentAssertions;

using LiteDB.Engine;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2816_DisposeFailureTests
    {
        [Fact]
        public void Log_metadata_failure_during_dispose_still_releases_the_owned_data_file()
        {
            using var file = new TempFile();
            var failure = Record.Exception(() => VerifyOwnedFileLifecycle(file.Filename));
            if (failure != null)
            {
                // The helper has already observed ownership while the database was
                // alive. Release leaked historical handles only after that observation,
                // so Windows temporary-file cleanup cannot replace the actual failure.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void VerifyOwnedFileLifecycle(string filename)
        {
            using var log = new UnavailableLog();
            var database = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                Filename = filename,
                LogStream = log
            }));
            database.CheckpointSize = 0;
            database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 31, ["payload"] = "committed before close" });
            database.Checkpoint();
            database.GetCollection("rows").FindById(31)["payload"].AsString.Should().Be("committed before close");

            log.FailNextLength = true;
            var closeFailure = Record.Exception(database.Dispose);
            log.Failures.Should().Be(1, "the log metadata operation must really fail during disposal");
            log.FailNextLength.Should().BeFalse("the simulated external storage fault has cleared");
            if (closeFailure != null)
            {
                closeFailure.ToString().Should().Contain(UnavailableLog.FailureMessage);
            }

            // Do not collect/finalize or retry Dispose before checking ownership.
            // This is a real exclusively opened OS file, not a mocked Dispose flag.
            var ownershipFailure = Record.Exception(() =>
            {
                using (File.Open(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            });
            GC.KeepAlive(database);
            ownershipFailure.Should().BeNull("Dispose must release its owned data-file handle before any finalization");
            using (var reopened = new LiteDatabase(filename))
            {
                var rows = reopened.GetCollection("rows");
                rows.Count().Should().Be(1);
                rows.FindById(31)["payload"].AsString.Should().Be("committed before close");
                rows.Insert(new BsonDocument { ["_id"] = 47, ["payload"] = "after recovery" });
            }
            using (var verified = new LiteDatabase(filename))
            {
                var rows = verified.GetCollection("rows");
                rows.Count().Should().Be(2);
                rows.FindById(31)["payload"].AsString.Should().Be("committed before close");
                rows.FindById(47)["payload"].AsString.Should().Be("after recovery");
            }
        }

        private sealed class UnavailableLog : MemoryStream
        {
            public const string FailureMessage = "issue 2816: log metadata temporarily unavailable";
            public bool FailNextLength;
            public int Failures;

            public override long Length
            {
                get
                {
                    if (FailNextLength)
                    {
                        FailNextLength = false;
                        Failures++;
                        throw new IOException(FailureMessage);
                    }
                    return base.Length;
                }
            }
        }
    }
}
