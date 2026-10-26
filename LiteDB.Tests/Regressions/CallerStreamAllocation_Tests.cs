using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    public class CallerStreamAllocation_Tests
    {
        [Theory]
        [MemberData(nameof(AllocationPublication_Tests.Cases), MemberType = typeof(AllocationPublication_Tests))]
        public void Caller_file_log_recovers_interleaved_allocations_without_changing_read_only_data(
            string password, string mode, int limit, string completion)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var recoveredFile = new TempFile();
            using (var seed = AllocationWorkload.Open(dataFile.Filename, password))
            {
                AllocationWorkload.Seed(seed, mode);
            }
            var original = File.ReadAllBytes(dataFile.Filename);
            using (var data = new FileStream(dataFile.Filename, FileMode.Open, FileAccess.Read))
            using (var log = new FileStream(logFile.Filename, FileMode.Create, FileAccess.ReadWrite))
            {
                using (var engine = new LiteEngine(new EngineSettings
                {
                    DataStream = data, LogStream = log, Password = password,
                    TransactionPageLimit = limit, CompactStorage = CompactStorageMode.Legacy
                }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    var emptyWrites = 0;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        if (page[BasePage.P_PAGE_TYPE] == (byte)PageType.Empty) emptyWrites++;
                    };
                    AllocationWorkload.Publish(db, mode, completion, stage: stage =>
                    {
                        if (stage == "committed")
                            emptyWrites.Should().BeGreaterThan(0, "the foreign reservations must reach WAL publication");
                    });
                    engine.SimulateDiskWriteFail = null;
                    AllocationWorkload.Verify(db, completion == "commit");
                }
                data.CanRead.Should().BeTrue();
                log.CanWrite.Should().BeTrue();
            }
            File.ReadAllBytes(dataFile.Filename).Should().Equal(original);
            var committedLog = File.ReadAllBytes(logFile.Filename);
            committedLog.Should().NotBeEmpty();

            for (var retry = 0; retry < 2; retry++)
            {
                using (var data = new FileStream(dataFile.Filename, FileMode.Open, FileAccess.Read))
                using (var log = new FileStream(logFile.Filename, FileMode.Open, FileAccess.ReadWrite))
                using (var db = new LiteDatabase(new LiteEngine(new EngineSettings
                {
                    DataStream = data, LogStream = log, Password = password
                })))
                {
                    AllocationWorkload.Verify(db, completion == "commit");
                }
                File.ReadAllBytes(dataFile.Filename).Should().Equal(original);
                File.ReadAllBytes(logFile.Filename).Should().Equal(committedLog);
            }

            // Materialize the retained pair independently to check the complete page
            // graph as well as the payload/index oracle used by read-only recovery.
            File.WriteAllBytes(recoveredFile.Filename, original);
            File.WriteAllBytes(FileHelper.GetLogFile(recoveredFile.Filename), committedLog);
            for (var retry = 0; retry < 2; retry++)
            {
                using var recovered = AllocationWorkload.Open(recoveredFile.Filename, password);
                AllocationWorkload.Verify(recovered, completion == "commit");
                recovered.Checkpoint();
                AllocationFileGraph.Verify(recoveredFile.Filename, password);
            }
            AllocationReachability.Verify(recoveredFile.Filename, password);
        }
    }
}
