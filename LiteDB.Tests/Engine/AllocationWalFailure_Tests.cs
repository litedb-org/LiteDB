using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationWalFailure_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var rollback in new[] { false, true })
            foreach (var failAt in new[] { 1, 5, 0 })
            foreach (var ioFailure in new[] { false, true })
                yield return new object[] { password, rollback, failAt, ioFailure };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Torn_allocation_publication_stops_before_another_commit_and_recovers_the_complete_graph(
            string password, bool rollback, int failAt, bool ioFailure)
        {
            using var file = new TempFile();
            using var crash = new TempFile();
            using (var data = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var log = new TornLog(FileHelper.GetLogFile(file.Filename), ioFailure))
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password,
                TransactionPageLimit = 1000, CompactStorage = CompactStorageMode.Legacy
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                AllocationWorkload.Seed(db, "hole");
                using var allocated = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                Exception ownerFailure = null;
                uint[] reservations = null;
                var owner = new Thread(() =>
                {
                    try
                    {
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("a").Insert(Enumerable.Range(100, 12).Select(AllocationWorkload.Document));
                        reservations = engine.GetMonitor().GetThreadTransaction().Pages.NewPages.ToArray();
                        allocated.Set();
                        release.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                        // Simulate an abandoned owner; its private pages must never become live.
                    }
                    catch (Exception ex) { ownerFailure = ex; allocated.Set(); }
                }) { IsBackground = true };
                owner.Start();
                try
                {
                    allocated.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    ownerFailure.Should().BeNull();
                    reservations.Length.Should().BeGreaterThan(5);
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("b").Insert(Enumerable.Range(200, 8).Select(AllocationWorkload.Document));
                    var foreignWrites = 0;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        var foreign = page[BasePage.P_PAGE_TYPE] == (byte)PageType.Empty &&
                            reservations.Contains(page.ReadUInt32(BasePage.P_PAGE_ID));
                        if (foreign) foreignWrites++;
                        if ((foreign && foreignWrites == failAt) ||
                            (failAt == 0 && page.ReadUInt32(BasePage.P_PAGE_ID) == 0))
                            log.TearNext = true;
                    };
                    Action publish = () => { if (rollback) db.Rollback(); else db.Commit(); };
                    publish.Should().Throw<IOException>().Where(ex => ex.ToString().Contains("torn allocation frame"));
                    foreignWrites.Should().BeGreaterThan(0, "the failure must occur inside foreign allocation publication");
                    log.Torn.Should().BeTrue();
                    log.TruncationFailed.Should().BeTrue("the torn frame must remain in the WAL");
                    var writes = log.Writes;
                    var dataAtFailure = ReadPhysical(file.Filename);
                    var logAtFailure = ReadPhysical(FileHelper.GetLogFile(file.Filename));
                    File.WriteAllBytes(crash.Filename, dataAtFailure);
                    File.WriteAllBytes(FileHelper.GetLogFile(crash.Filename), logAtFailure);
                    Action later = () => db.UserVersion = 3082;
                    later.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure.*");
                    log.Writes.Should().Be(writes, "later publication must stop before writing");
                    ReadPhysical(file.Filename).Should().Equal(dataAtFailure);
                    ReadPhysical(FileHelper.GetLogFile(file.Filename)).Should().Equal(logAtFailure);
                }
                finally
                {
                    release.Set();
                    owner.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
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

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Failed_safepoint_rollback_publishes_foreign_reservations_before_later_commits(string password)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, TransactionPageLimit = 1000,
                CompactStorage = CompactStorageMode.Legacy
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                AllocationWorkload.Seed(db, "hole");
                AllocationWorkload.Publish(db, "hole", stage: stage =>
                {
                    if (stage != "allocated") return;
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("b").Insert(Enumerable.Range(300, 8).Select(AllocationWorkload.Document));
                    var transaction = engine.GetMonitor().GetThreadTransaction();
                    transaction.Pages.NewPages.Should().NotBeEmpty();
                    transaction.MaxTransactionSize = 1;
                    var refused = false;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        refused = true;
                        throw new UnauthorizedAccessException("safepoint refused before writing");
                    };
                    Action safepoint = () => transaction.Safepoint();
                    safepoint.Should().Throw<UnauthorizedAccessException>();
                    refused.Should().BeTrue();
                    Action read = () => db.GetCollection("b").FindById(1);
                    read.Should().Throw<LiteException>().WithMessage("*can only be rolled back*");
                    var foreignWrites = 0;
                    var ownReservations = transaction.Pages.NewPages.ToArray();
                    engine.SimulateDiskWriteFail = page =>
                    {
                        if (page[BasePage.P_PAGE_TYPE] == (byte)PageType.Empty &&
                            !ownReservations.Contains(page.ReadUInt32(BasePage.P_PAGE_ID))) foreignWrites++;
                    };
                    Action commit = () => db.Commit();
                    commit.Should().Throw<LiteException>().WithMessage("*can only be rolled back*");
                    foreignWrites.Should().BeGreaterThan(0,
                        "automatic rollback must publish other active owners' reservations too");
                    engine.SimulateDiskWriteFail = null;
                    engine.GetMonitor().GetThreadTransaction().Should().BeNull("the failed transaction has rolled back");
                    // Publish now performs a successful later commit with the foreign owner still idle.
                });
            }
            for (var retry = 0; retry < 2; retry++)
            {
                using var recovered = AllocationWorkload.Open(file.Filename, password);
                AllocationWorkload.Verify(recovered);
                recovered.GetCollection("b").Find(Query.GTE("_id", 300)).Should().BeEmpty();
                recovered.Checkpoint();
                AllocationFileGraph.Verify(file.Filename, password);
            }
            AllocationReachability.Verify(file.Filename, password);
        }

        private static byte[] ReadPhysical(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }

        private sealed class TornLog : FileStream
        {
            private readonly bool _ioFailure;
            internal bool TearNext, Torn, TruncationFailed;
            internal int Writes;

            internal TornLog(string filename, bool ioFailure)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 1)
            {
                _ioFailure = ioFailure;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Writes++;
                if (TearNext && count > 0)
                {
                    TearNext = false;
                    base.Write(buffer, offset, Math.Max(1, count / 2));
                    Torn = true;
                    if (_ioFailure) throw new IOException("torn allocation frame");
                    throw new InvalidOperationException("torn allocation frame");
                }
                base.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                if (Torn)
                {
                    TruncationFailed = true;
                    throw new InvalidOperationException("allocation WAL truncation failed");
                }
                base.SetLength(value);
            }
        }
    }
}
