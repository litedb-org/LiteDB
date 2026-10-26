using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationPublicationFailure_Tests
    {
        [Theory]
        [InlineData(null, 1)]
        [InlineData("secret", 1)]
        [InlineData(null, 5)]
        [InlineData("secret", 5)]
        [InlineData(null, 0)]
        [InlineData("secret", 0)]
        public void Failed_foreign_image_publication_refuses_pending_header_free_and_later_header_commits(string password, int failAt)
        {
            using var file = new TempFile();
            using var crash = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, TransactionPageLimit = 1000,
                CompactStorage = CompactStorageMode.Legacy
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                AllocationWorkload.Seed(db, "hole");
                var monitor = (TransactionMonitor)typeof(LiteEngine)
                    .GetField("_monitor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                using var allocated = new ManualResetEventSlim();
                using var prepared = new ManualResetEventSlim();
                using var complete = new ManualResetEventSlim();
                uint[] reservations = null;
                Exception ownerFailure = null;
                Exception writerFailure = null;
                var owner = new Thread(() =>
                {
                    try
                    {
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("a").Insert(Enumerable.Range(100, 12).Select(AllocationWorkload.Document));
                        reservations = monitor.GetTransaction(false, false, out _).Pages.NewPages.ToArray();
                        reservations.Length.Should().BeGreaterThan(5);
                        allocated.Set();
                        complete.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                        // Abandon the reservation; no owner commit or rollback.
                    }
                    catch (Exception ex) { ownerFailure = ex; allocated.Set(); }
                }) { IsBackground = true };
                var writer = new Thread(() =>
                {
                    try
                    {
                        db.BeginTrans().Should().BeTrue();
                        var changed = AllocationWorkload.Document(1);
                        changed["payload"] = new string('z', 2500);
                        db.GetCollection("cold").Update(changed).Should().BeTrue();
                        monitor.GetTransaction(false, false, out _).Pages.HeaderChanged.Should().BeFalse(
                            "the waiting writer must reach a header-free commit if admitted");
                        prepared.Set();
                        complete.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                        Action commit = () => db.Commit();
                        commit.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure.*");
                    }
                    catch (Exception ex) { writerFailure = ex; prepared.Set(); }
                }) { IsBackground = true };
                owner.Start();
                writer.Start();
                try
                {
                    allocated.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    prepared.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    ownerFailure.Should().BeNull();
                    writerFailure.Should().BeNull();
                    var emptyWrites = 0;
                    var writes = 0;
                    var failed = false;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        writes++;
                        var empty = page[BasePage.P_PAGE_TYPE] == (byte)PageType.Empty;
                        var header = page[BasePage.P_PAGE_TYPE] == (byte)PageType.Header;
                        if (empty) reservations.Should().Contain(page.ReadUInt32(BasePage.P_PAGE_ID));
                        if ((empty && ++emptyWrites == failAt) || (header && failAt == 0))
                        {
                            // Preserve the physical crash prefix before exception cleanup can run.
                            File.WriteAllBytes(crash.Filename, ReadPhysical(file.Filename));
                            File.WriteAllBytes(FileHelper.GetLogFile(crash.Filename),
                                ReadPhysical(FileHelper.GetLogFile(file.Filename)));
                            failed = true;
                            throw new IOException("foreign allocation publication failed");
                        }
                    };
                    Action publish = () => db.UserVersion = 3082;
                    publish.Should().Throw<IOException>().WithMessage("foreign allocation publication failed");
                    failed.Should().BeTrue("CommitHeader must stage a foreign image before failure");
                    emptyWrites.Should().BeGreaterThan(0);
                    var writesAtFailure = writes;
                    var dataAtFailure = ReadPhysical(file.Filename);
                    var logAtFailure = ReadPhysical(FileHelper.GetLogFile(file.Filename));
                    var logExists = File.Exists(FileHelper.GetLogFile(file.Filename));
                    complete.Set();
                    writer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    writerFailure.Should().BeNull();
                    // A later callback-only header commit is refused after the pending commit.
                    publish.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure.*");
                    Action begin = () => db.BeginTrans();
                    begin.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure.*");
                    writes.Should().Be(writesAtFailure, "neither later commit may reach WAL publication");
                    ReadPhysical(file.Filename).Should().Equal(dataAtFailure);
                    ReadPhysical(FileHelper.GetLogFile(file.Filename)).Should().Equal(logAtFailure);
                    File.Exists(FileHelper.GetLogFile(file.Filename)).Should().Be(logExists);
                }
                finally
                {
                    complete.Set();
                    owner.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    writer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
                }
                ownerFailure.Should().BeNull();
            }
            foreach (var filename in new[] { crash.Filename, file.Filename })
            {
                for (var retry = 0; retry < 2; retry++)
                {
                    using var recovered = AllocationWorkload.Open(filename, password);
                    AllocationWorkload.Verify(recovered, committed: false, headerPublished: false);
                    recovered.Checkpoint();
                    AllocationFileGraph.Verify(filename, password);
                }
                AllocationReachability.Verify(filename, password);
            }
        }

        private static byte[] ReadPhysical(string filename)
        {
            if (!File.Exists(filename)) return Array.Empty<byte>();
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
