#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 11 of docs/decisions/durability-policy.md: every WAL generation starts with a header
    /// frame, a copy of the data header its frames depend on, in both modes. Recovery restores a data
    /// header a power loss dropped from it, and an open never initializes a new database over the WAL
    /// of one whose data file lost its header: it restores the header or refuses, changing neither
    /// file. Before, an empty data file beside such a WAL became a new, empty database and recovery
    /// silently discarded every frame (for example after <c>durable commits=false</c> on storage that
    /// cannot sync, #2242, and a power loss).
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public partial class HeaderFrame_Tests
    {
        private const int Marker = 52; // trailer offset of the frame marker
        private const uint HeaderFrameMagic = 0x31524448;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Every_wal_generation_starts_with_a_copy_of_the_data_header(bool durable)
        {
            using var file = new TempFile();
            var connection = $"Filename={file.Filename};Durable Commits={durable}";
            using (var db = new LiteDatabase(connection))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                AssertHeaderFrame(file.Filename);
                db.GetCollection("rows").Insert(Row(2));
                AssertHeaderFrame(file.Filename);
                Frames(file.Filename).Should().BeGreaterThan(2, "one header frame, then the commits' frames");

                db.Checkpoint();
                LogLength(file.Filename).Should().Be(0, "a checkpoint whose data sync succeeded empties the WAL");
                var drained = SyncPowerLossModel.ReadShared(file.Filename).Take(PAGE_SIZE).ToArray();
                db.GetCollection("rows").Insert(Row(3));
                AssertHeaderFrame(file.Filename);
                HeaderPage(file.Filename).Should().Equal(drained, "the next generation copies the header its checkpoint rotated");
            }
            using (var reopened = new LiteDatabase(connection))
                reopened.GetCollection("rows").Count().Should().Be(3);
        }

        /// <summary>
        /// The owner's second opinion, finding 1: storage where neither file syncs, commits opted out
        /// of durability, then a power loss after the OS wrote the log back but not the data file's
        /// header (a new data file left empty). The open restores the header and every commit.
        /// </summary>
        [Theory]
        [InlineData(0)] // the data file left empty
        [InlineData(1)] // its header page never written back (zeros)
        [InlineData(2)] // its header page torn: its first sector never written back
        public void Opted_out_commits_survive_a_data_file_that_lost_its_header(int damage)
        {
            using var file = new TempFile();
            var connection = $"Filename={file.Filename};Durable Commits=false";
            NativeFileSync.SimulateErrno = _ => 22; // neither file syncs (#2242)
            try
            {
                using var db = new LiteDatabase(connection);
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 50).Select(Row));
                db.GetCollection("rows").EnsureIndex("value");
            }
            finally { NativeFileSync.SimulateErrno = null; }

            var data = File.ReadAllBytes(file.Filename);
            var header = data.Take(PAGE_SIZE).ToArray();
            if (damage == 0) data = new byte[0];
            else Array.Clear(data, 0, damage == 1 ? PAGE_SIZE : 512);
            if (damage != 0) data.Take(PAGE_SIZE).Should().NotEqual(header, "the damage changes the header");
            File.WriteAllBytes(file.Filename, data);

            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("rows").Count().Should().Be(50);
                db.GetCollection("rows").Count(Query.EQ("value", 3)).Should().Be(7);
                db.GetCollection("rows").Insert(Row(51));
            }
            using var reopened = new LiteDatabase(connection);
            reopened.GetCollection("rows").Count().Should().Be(51);
        }

        [Fact]
        public void Read_only_open_reads_a_lost_header_from_the_log_without_writing()
        {
            using var file = new TempFile();
            WriteUncheckpointed(file.Filename, 20);
            var data = File.ReadAllBytes(file.Filename);
            Array.Clear(data, 0, PAGE_SIZE);
            File.WriteAllBytes(file.Filename, data);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));

            using (var db = new LiteDatabase($"Filename={file.Filename};ReadOnly=true"))
                db.GetCollection("rows").Count().Should().Be(20);

            File.ReadAllBytes(file.Filename).Should().Equal(data, "a read-only open writes nothing");
            File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
        }

        /// <summary>
        /// Recovery never reads the header frame as a page, whatever the header it copies holds: here
        /// its page carries the confirmation flag, which as a page would be a confirmation with no
        /// pages and end recovery before every commit after it.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Header_frame_is_never_read_as_a_page(bool readOnly)
        {
            using var file = new TempFile();
            WriteUncheckpointed(file.Filename, 20);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            var frame = log.Take(WalChecksum.FrameSize).ToArray();
            var page = new BufferSlice(frame, 0, PAGE_SIZE);
            page[BasePage.P_IS_CONFIRMED] = 1;
            PageChecksum.Write(page);
            WalChecksum.PrepareHeaderFrame(frame, frame.Skip(PAGE_SIZE + 8).Take(16).ToArray());
            WalChecksum.ReadHeaderFrame(frame).Should().NotBeNull("the edited copy is still a valid header frame");
            Buffer.BlockCopy(frame, 0, log, 0, frame.Length);
            File.WriteAllBytes(logName, log);

            using var db = new LiteDatabase($"Filename={file.Filename};ReadOnly={readOnly}");
            db.GetCollection("rows").Count().Should().Be(20);
            db.Execute("SELECT $ FROM $database").Single()["recoveryInvalidWalTail"].AsBoolean.Should().BeFalse();
        }

        [Fact]
        public void Read_only_open_of_an_empty_data_file_explains_the_restore_and_writes_nothing()
        {
            using var file = new TempFile();
            WriteUncheckpointed(file.Filename, 5);
            File.WriteAllBytes(file.Filename, new byte[0]);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));

            Action open = () => new LiteDatabase($"Filename={file.Filename};ReadOnly=true").Dispose();
            open.Should().Throw<LiteException>().WithMessage("*data file is empty*Open it once without read-only*");

            new FileInfo(file.Filename).Length.Should().Be(0);
            File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
            using var writable = new LiteDatabase(file.Filename);
            writable.GetCollection("rows").Count().Should().Be(5);
        }

        /// <summary>
        /// The header frame names pages the data file held when the WAL started (here after a
        /// checkpoint): an empty or missing data file lost them, so no restore can be complete. The
        /// open refuses, changing neither file, instead of starting a new database over the WAL.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Log_of_a_database_whose_data_file_is_gone_is_never_initialized_over(bool missing)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 30).Select(Row));
                db.Checkpoint();
                db.GetCollection("rows").Insert(Row(31));
            }
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            if (missing) File.Delete(file.Filename);
            else File.WriteAllBytes(file.Filename, new byte[0]);

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            open.Should().Throw<LiteException>().WithMessage($"*data file is {(missing ? "missing" : "empty")}*move the log file aside*");

            File.Exists(file.Filename).Should().Be(!missing, "the refused open creates no data file");
            if (!missing) new FileInfo(file.Filename).Length.Should().Be(0);
            File.ReadAllBytes(logName).Should().Equal(log);
        }

        /// <summary>
        /// Frame 0 of the log never reached the device while later frames did: no header frame can be
        /// read, but the log holds WAL frames, so the empty data file is not initialized over them.
        /// The same for an encrypted log opened without its password. Review finding B2.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Log_whose_header_frame_cannot_be_read_is_never_initialized_over(bool encryptedWithoutPassword)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var password = encryptedWithoutPassword ? ";Password=secret" : "";
            using (var db = new LiteDatabase($"Filename={file.Filename};Durable Commits=false{password}"))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 12).Select(Row));
            }
            File.WriteAllBytes(file.Filename, new byte[0]);
            var log = File.ReadAllBytes(logName);
            if (!encryptedWithoutPassword) Array.Clear(log, 0, WalChecksum.FrameSize); // frame 0 never written back
            File.WriteAllBytes(logName, log);

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            open.Should().Throw<LiteException>().WithMessage("*log file holds WAL frames*");
            new FileInfo(file.Filename).Length.Should().Be(0, "the refused open writes nothing");
            File.ReadAllBytes(logName).Should().Equal(log);

            if (encryptedWithoutPassword)
            {
                using var db = new LiteDatabase($"Filename={file.Filename}{password}");
                db.GetCollection("rows").Count().Should().Be(12, "with its password the header is restored");
            }
        }

        /// <summary>
        /// An intact data header of another salt always wins: the WAL beside it is a stale generation
        /// (its frames were checkpointed before the salt rotated), discarded as before.
        /// </summary>
        [Fact]
        public void Stale_wal_generation_beside_an_intact_header_is_discarded()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            byte[] stale;
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 10).Select(Row));
                stale = SyncPowerLossModel.ReadShared(logName);
                db.Checkpoint();
                db.GetCollection("rows").Insert(Row(11));
                db.Checkpoint();
            }
            File.WriteAllBytes(logName, stale);

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(11);
            var info = reopened.Execute("SELECT $ FROM $database").Single();
            info["recoveryInvalidWalTail"].AsBoolean.Should().BeTrue("the stale header frame fails the salt check");
            info["recoveryDiscardedWalBytes"].AsInt64.Should().BeGreaterThan(0);
        }

        /// <summary>
        /// A write that changes nothing (an update that matched no document) writes no frame, so it
        /// needs no durability proof: on a log that cannot sync it neither throws nor records a failure.
        /// </summary>
        [Fact]
        public void Write_that_changes_nothing_needs_no_log_proof()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").Insert(Row(1));
            DurableLogs.Forget(Path.GetFullPath(FileHelper.GetLogFile(file.Filename)));
            NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 22 : 0;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").Update(Row(99)).Should().BeFalse();
                db.GetCollection("rows").DeleteMany(x => x["_id"] == 99).Should().Be(0);
                var info = db.Execute("SELECT $ FROM $database").Single();
                info["writeFailure"].IsNull.Should().BeTrue();
                info["readOnly"].AsBoolean.Should().BeFalse();
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        /// <summary>
        /// Only a log the engine opens has its directory synced by the proof, so only its path is
        /// remembered as proven: a caller's log stream would otherwise let a later engine that opens the
        /// same path skip the directory sync.
        /// </summary>
        [Fact]
        public void Caller_log_stream_is_not_remembered_as_proven()
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            DurableLogs.Forget(logName);
            using (var data = new FileStream(file.Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite))
            using (var log = new FileStream(logName, FileMode.OpenOrCreate, FileAccess.ReadWrite))
            using (var db = new LiteDatabase(data, logStream: log))
            {
                db.GetCollection("rows").Insert(Row(1));
            }
            DurableLogs.Contains(logName).Should().BeFalse();

            using (var db = new LiteDatabase(file.Filename)) db.GetCollection("rows").Insert(Row(2));
            DurableLogs.Contains(logName).Should().BeTrue();
        }

        /// <summary>
        /// Decision 14: the data barrier before a first commit runs once per shared connection, and a
        /// connection that found the data file cannot sync does not retry it per operation: its
        /// operations pay nothing for it. Its commits stay durable in the WAL (decision 4).
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Shared_connection_runs_the_data_barrier_once(bool dataSyncs)
        {
            using var file = new TempFile();
            var dataName = Path.GetFullPath(file.Filename);
            var runs = 0;
            DiskService.DataBarrierRan = path => { if (string.Equals(path, dataName, StringComparison.OrdinalIgnoreCase)) runs++; };
            NativeFileSync.SimulateErrno = path => !dataSyncs && Path.GetFullPath(path) == dataName ? 22 : 0;
            try
            {
                using var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                db.CheckpointSize = 0;
                for (var id = 1; id <= 5; id++) db.GetCollection("rows").Insert(Row(id));
                runs.Should().Be(dataSyncs ? 1 : 0, dataSyncs ? "once for the connection" : "creating the database found that its data file cannot sync");
                db.GetCollection("rows").Count().Should().Be(5);
                db.Execute("SELECT $ FROM $database").Single()["durableLogFlush"].AsBoolean.Should().BeTrue();
            }
            finally
            {
                DiskService.DataBarrierRan = null;
                NativeFileSync.SimulateErrno = null;
            }
        }

        /// <summary>
        /// A header frame whose write fails part-way is truncated like a failed append. The failed
        /// write is recorded either way (decision 6: the device is taken as bad) and the engine
        /// continues read-only; if the truncation fails too, the torn frame stays, and the next open
        /// discards it: no acknowledged commit depended on it.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_header_frame_write_is_truncated_or_stops_the_engine(bool truncationFails)
        {
            var data = new MemoryStream();
            var log = new TearingLog();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                db.Checkpoint();
                log.Length.Should().Be(0);
                log.TearNextFrame = true;
                log.SetLengthFails = truncationFails;
                Action insert = () => db.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>();
                log.SetLengthFails = false;

                var info = db.Execute("SELECT $ FROM $database").Single();
                info["readOnly"].AsBoolean.Should().BeTrue();
                info["writeFailure"].IsNull.Should().BeFalse();
                log.Length.Should().Be(truncationFails ? WalChecksum.FrameSize / 2 : 0,
                    truncationFails ? "the torn frame 0 stays: nothing may be appended behind it" : "the torn bytes were truncated");
                db.GetCollection("rows").Count().Should().Be(1);
                var recorded = info["readOnlyReason"].AsString;
                ReadOnlyAfterWriteFailure.AssertWriteRefused(() => db.GetCollection("rows").Insert(Row(3)), recorded);
                log.Length.Should().Be(truncationFails ? WalChecksum.FrameSize / 2 : 0, "the read-only engine wrote nothing more");
            }
            using var reopened = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            reopened.Query("rows", Query.All()).ToList().Select(x => x["_id"].AsInt32).Should().Equal(1);
            reopened.Insert("rows", new[] { Row(4) }, BsonAutoId.Int32);
            reopened.Query("rows", Query.All()).ToList().Select(x => x["_id"].AsInt32).Should().Equal(1, 4);
        }

        private sealed class TearingLog : MemoryStream
        {
            internal bool TearNextFrame, SetLengthFails;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (TearNextFrame && count == WalChecksum.FrameSize)
                {
                    TearNextFrame = false;
                    base.Write(buffer, offset, count / 2);
                    throw new IOException("injected torn write");
                }
                base.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                if (SetLengthFails) throw new IOException("injected truncation failure");
                base.SetLength(value);
            }
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id % 7 };

        /// <summary>Leave the files a process crash leaves after <paramref name="rows"/> commits: the WAL not checkpointed.</summary>
        private static void WriteUncheckpointed(string filename, int rows)
        {
            using var scratch = new TempFile();
            byte[] data, log;
            using (var db = new LiteDatabase($"Filename={scratch.Filename};Durable Commits=false"))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, rows).Select(Row));
                data = SyncPowerLossModel.ReadShared(scratch.Filename);
                log = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(scratch.Filename));
            }
            File.Delete(FileHelper.GetLogFile(scratch.Filename));
            File.WriteAllBytes(filename, data);
            File.WriteAllBytes(FileHelper.GetLogFile(filename), log);
        }

        private static void AssertHeaderFrame(string filename)
        {
            var log = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(filename));
            log.Length.Should().BeGreaterOrEqualTo(WalChecksum.FrameSize);
            BitConverter.ToUInt32(log, PAGE_SIZE + Marker).Should().Be(HeaderFrameMagic, "frame 0 is the header frame");
            var data = SyncPowerLossModel.ReadShared(filename);
            log.Take(PAGE_SIZE).Should().Equal(data.Take(PAGE_SIZE), "it holds the data header byte for byte");
        }

        private static byte[] HeaderPage(string filename) => SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(filename)).Take(PAGE_SIZE).ToArray();

        private static long LogLength(string filename)
        {
            var log = FileHelper.GetLogFile(filename);
            return File.Exists(log) ? new FileInfo(log).Length : 0;
        }

        private static long Frames(string filename) => LogLength(filename) / WalChecksum.FrameSize;
    }
}
#endif
