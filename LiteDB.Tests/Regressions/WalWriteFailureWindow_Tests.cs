using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
#if DEBUG || TESTING
    [Trait("Category", "IoSafety")]
    public class WalWriteFailureWindow_Tests
    {
        [Theory]
        [InlineData("anchor-flush", true)]
        [InlineData("anchor-flush", false)]
        [InlineData("rewrite", true)]
        [InlineData("rewrite", false)]
        [InlineData("batch-flush", true)]
        [InlineData("batch-flush", false)]
        [InlineData("length", true)]
        [InlineData("length", false)]
        [InlineData("sync-probe", true)]
        [InlineData("sync-probe", false)]
        [InlineData("padding", true)]
        [InlineData("padding", false)]
        [InlineData("seek", true)]
        [InlineData("seek", false)]
        [InlineData("confirmation-hook", false)]
        [InlineData("confirmation-callback", false)]
        public void Failure_publishes_stop_before_a_queued_writer_can_confirm(string boundary, bool ioFailure)
        {
            var legacy = boundary == "anchor-flush" || boundary == "rewrite";
            using var data = Seed(legacy);
            using var log = new FaultLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            var state = new EngineState(null, settings);
            using var disk = new DiskService(settings, state, new[] { 2 });
            var transaction = new TransactionPages { TransactionID = legacy ? 1u : 5u };
            disk.WriteLogDisk(Pages(disk, data, 5, false), (id, position) =>
                transaction.DirtyPages[id] = new PagePosition(id, position));

            using var attempted = new ManualResetEventSlim();
            Thread queued = null;
            Exception queuedFailure = null;
            var acknowledged = false;
            long failureLength = -1;
            Action queue = () =>
            {
                failureLength = log.Length;
                queued = new Thread(() =>
                {
                    state.Validate();
                    attempted.Set();
                    try
                    {
                        disk.WriteLogDisk(Pages(disk, data, 9, true));
                        acknowledged = true;
                    }
                    catch (Exception ex) { queuedFailure = ex; }
                }) { IsBackground = true };
                queued.Start();
                attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            var teardownWindow = false;
            var finishedBeforeTeardown = false;
            state.AfterFailedWalWrite = () =>
            {
                teardownWindow = true;
                failureLength = log.Length;
                state.Stopped.Should().BeTrue("stop must precede writer release and teardown");
                finishedBeforeTeardown = queued.Join(TimeSpan.FromSeconds(10));
            };
            Action fail = () =>
            {
                queue();
                throw ioFailure ? new IOException("injected " + boundary) :
                    new InvalidOperationException("injected " + boundary);
            };
            var hits = 0;
            var reached = new HashSet<string>();
            state.AtCrashPoint = point =>
            {
                if (point == "wal-rebase-before-anchor-flush" || point == "wal-rebase-before-rewrite" ||
                    point == "wal-before-batch-flush") reached.Add(point);
                if (point == "wal-confirmation-after-write" && boundary == "confirmation-hook")
                {
                    state.AtCrashPoint = null;
                    hits++;
                    fail();
                }
            };
            // These stream faults exercise the actual I/O operations rather than throwing before
            // them. MemoryStream subclasses deliberately take the non-buffering admission path.
            if (boundary == "sync-probe") disk.RegisterFreeLogPosition(0);
            log.Arm(boundary, () => { hits++; fail(); });
            Action<uint, long> written = boundary == "confirmation-callback"
                ? (Action<uint, long>)((id, position) => { hits++; fail(); }) : null;
            Exception failure = null;
            try
            {
                if (legacy) disk.WriteLogDisk(Pages(disk, data, 1, true), written, transaction, () => 6);
                else if (boundary == "length" || boundary == "seek")
                    disk.WriteLogDisk(TwoPages(disk, data, log), written);
                else disk.WriteLogDisk(Pages(disk, data, 6, boundary.StartsWith("confirmation-")), written);
            }
            catch (Exception ex) { failure = ex; }
            log.Disarm();
            queued.Should().NotBeNull("the fault must be reached");
            queued.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            hits.Should().Be(1, "the actual I/O fault must run (markers: {0})", string.Join(",", reached));
            failure.Should().BeOfType<IOException>();
            state.Stopped.Should().BeTrue();
            teardownWindow.Should().BeTrue();
            finishedBeforeTeardown.Should().BeTrue("the queued writer must finish before stop completion");
            acknowledged.Should().BeFalse();
            queuedFailure.Should().BeOfType<IOException>().Which.Message.Should().StartWith("Engine closed");
            log.Length.Should().Be(failureLength, "no writer may append behind the failed batch");
            var image = log.ToArray();
            Action later = () => disk.WriteLogDisk(Pages(disk, data, 10, true));
            later.Should().Throw<IOException>();
            log.ToArray().Should().Equal(image);
            AssertFileRecovery(data.ToArray(), image);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Partial_promotion_journal_stops_before_another_writer_enters(bool ioFailure)
        {
            using var data = Seed(false);
            using var log = new FaultLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            var state = new EngineState(null, settings);
            using var disk = new DiskService(settings, state, new[] { 2 });
            disk.WriteLogDisk(Pages(disk, data, 1, false));
            using var attempted = new ManualResetEventSlim();
            Exception refused = null;
            Thread writer = null;
            var hits = 0;
            log.Arm("journal", () =>
            {
                hits++;
                writer = new Thread(() =>
                {
                    state.Validate();
                    attempted.Set();
                    try { disk.WriteLogDisk(Pages(disk, data, 9, true)); }
                    catch (Exception ex) { refused = ex; }
                }) { IsBackground = true };
                writer.Start();
                attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                throw ioFailure ? new IOException("injected journal") : new InvalidOperationException("injected journal");
            });
            Exception failure = null;
            try { disk.PromoteFileFormat(HeaderPage.COMPACT_FILE_VERSION); }
            catch (Exception ex) { failure = ex; }
            log.Disarm();
            writer.Should().NotBeNull("the journal write must be reached");
            writer.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            hits.Should().Be(1);
            failure.Should().BeOfType<IOException>();
            state.Stopped.Should().BeTrue();
            refused.Should().BeOfType<IOException>();
            var image = log.ToArray();
            Action later = () => disk.WriteLogDisk(Pages(disk, data, 10, true));
            later.Should().Throw<IOException>();
            log.ToArray().Should().Equal(image);
            AssertFileRecovery(data.ToArray(), image);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Maintenance_queued_before_stop_does_not_write_or_sync_after_it(bool promotion)
        {
            using var data = Seed(false);
            using var log = new FaultLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            var state = new EngineState(null, settings);
            using var disk = new DiskService(settings, state, new[] { 2 });
            disk.WriteLogDisk(Pages(disk, data, 1, false));
            using var attempted = new ManualResetEventSlim();
            Exception refused = null;
            var worker = new Thread(() =>
            {
                state.Validate();
                attempted.Set();
                try
                {
                    if (promotion) disk.PromoteFileFormat(HeaderPage.COMPACT_FILE_VERSION);
                    else disk.SyncLogBeforeCheckpoint();
                }
                catch (Exception ex) { refused = ex; }
            }) { IsBackground = true };
            var before = log.ToArray();
            var beforeData = data.ToArray();
            lock (disk.WalWriterLock)
            {
                worker.Start();
                attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                state.BeginStop(new IOException("injected earlier writer failure"));
            }
            worker.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            refused.Should().BeOfType<IOException>().Which.Message.Should().StartWith("Engine closed");
            data.ToArray().Should().Equal(beforeData);
            log.ToArray().Should().Equal(before);
            AssertFileRecovery(beforeData, before);
        }

        private static IEnumerable<PageBuffer> TwoPages(DiskService disk, MemoryStream data, FaultLog log)
        {
            foreach (var page in Pages(disk, data, 6, false)) yield return page;
            log.SecondPage = true;
            foreach (var page in Pages(disk, data, 6, false)) yield return page;
        }

        private static IEnumerable<PageBuffer> Pages(DiskService disk, MemoryStream data, uint id, bool confirmed)
        {
            var page = disk.NewPage();
            // A real, unchanged collection page makes even an outcome-unknown confirmation
            // recoverable. Only the seeded document is permitted by the independent model.
            Buffer.BlockCopy(data.ToArray(), PAGE_SIZE, page.Array, page.Offset, PAGE_SIZE);
            page.Write(id, BasePage.P_TRANSACTION_ID);
            page.Write(confirmed, BasePage.P_IS_CONFIRMED);
            yield return page;
        }

        private static MemoryStream Seed(bool legacy)
        {
            var data = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = data, CompactStorage = CompactStorageMode.Legacy
            })))
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
            if (legacy) ChecksumTestFiles.MakeLegacy(data, new MemoryStream(), null, 8);
            data.Position = 0;
            return data;
        }

        private static void AssertFileRecovery(byte[] data, byte[] log)
        {
            using var file = new TempFile();
            var wal = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(file.Filename, data);
            File.WriteAllBytes(wal, log);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var engine = new LiteEngine(new EngineSettings
                    {
                        Filename = file.Filename, ReadOnly = true, LegacyIndexScan = true
                    });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    db.GetCollection("docs").FindAll().Should().ContainSingle()
                        .Which["value"].AsInt32.Should().Be(7);
                }
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(wal).Should().Equal(log);
            }
            finally { File.Delete(wal); }
        }

        private sealed class FaultLog : MemoryStream, IDurableStream
        {
            private string _boundary;
            private Action _fail;
            private int _writes;
            internal bool SecondPage;
            internal void Arm(string boundary, Action fail) { _boundary = boundary; _fail = fail; _writes = 0; }
            internal void Disarm() => _boundary = null;
            private void Fail()
            {
                _boundary = null;
                _fail();
            }
            public override long Length
            {
                get
                {
                    if (_boundary == "length" && SecondPage) Fail();
                    return base.Length;
                }
            }
            public override long Position
            {
                get => base.Position;
                set
                {
                    if (_boundary == "seek" && SecondPage) Fail();
                    base.Position = value;
                }
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _writes++;
                if ((_boundary == "rewrite" && _writes == 2) ||
                    (_boundary == "journal" && count == PAGE_SIZE))
                {
                    base.Write(buffer, offset, Math.Min(count, 127));
                    Fail();
                }
                base.Write(buffer, offset, count);
            }
            public override void SetLength(long value)
            {
                if (_boundary == "padding") Fail();
                base.SetLength(value);
            }
            public override void Flush()
            {
                if (_boundary == "batch-flush" || _boundary == "anchor-flush" || _boundary == "sync-probe") Fail();
                base.Flush();
            }
            public void FlushToDisk() => Flush();
        }
    }
#endif
}
