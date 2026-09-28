#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Storage where neither file syncs (#2242) empties the WAL on a full checkpoint, as before
    /// #2818, with its backfill in the data file's OS cache only. Commits acknowledged as durable
    /// before the storage stopped syncing stay recoverable only while no log sync precedes a data
    /// sync. A fresh engine's first log sync came first when its open repaired a torn WAL tail or
    /// its checkpoint journaled the header: once the storage synced again, that sync made the
    /// earlier truncation durable, never the backfill, and a power loss (each file as of its last
    /// successful sync) lost those commits. An engine now proves the data file before its first
    /// log sync of any kind. A checkpoint now also writes only to a data file that just synced, so
    /// this engine no longer leaves such a truncation behind: the first two tests check that the
    /// acknowledged commits stay recoverable, and the torn-header tests reach a header journal in
    /// the OS cache only through a log that stops syncing.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class FreshEngineLogSync_Tests
    {
        [Fact]
        public void Open_repairing_a_torn_tail_keeps_commits_acknowledged_durable()
        {
            using var file = new TempFile();
            using var power = TruncatedWhileNothingSynced(file.Filename);
            // A writer that crashed mid-append left a partial frame.
            using (var log = new FileStream(FileHelper.GetLogFile(file.Filename), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                log.Write(Enumerable.Repeat((byte)0x5A, 100).ToArray(), 0, 100);

            power.DataFails = power.LogFails = false; // the storage syncs again
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Count().Should().Be(Rows);
            }
            power.AfterPowerLoss(Values).Should().Equal(new[] { 5 }, "value 5 was acknowledged durable");
            File.Delete(FileHelper.GetLogFile(file.Filename));
        }

        [Fact]
        public void Checkpoint_journal_does_not_make_an_unsynced_truncation_durable_first()
        {
            using var file = new TempFile();
            using var power = TruncatedWhileNothingSynced(file.Filename);

            power.DataFails = power.LogFails = false;
            (byte[] Data, byte[] Log) image = default;
            var settings = new EngineSettings { Filename = file.Filename };
            // Before the checkpoint's own data sync.
            settings.CheckpointStage = stage => { if (stage == "data-page" && image.Data == null) image = power.Capture(); };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.Checkpoint();
            }
            image.Data.Should().NotBeNull();
            FilePowerLossModel.Open(image, Values).Should().Equal(new[] { 5 }, "value 5 was acknowledged durable");
            File.Delete(FileHelper.GetLogFile(file.Filename));
        }

        /// <summary>
        /// The one log sync that must precede the data proof: an open that repairs a torn header from
        /// its journal makes the journal durable first. Proving the data file before it made the torn
        /// header durable while the journal, its only repair, was still in the OS cache. The journal
        /// reaches the OS cache only where the log stops syncing: a checkpoint writes only to a data
        /// file that just synced, so the data file still syncs here.
        /// </summary>
        [Fact]
        public void Open_repairing_a_torn_header_makes_its_journal_durable_first()
        {
            using var file = new TempFile();
            using var power = new FilePowerLossModel(file.Filename);
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++)
                    db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));
            }

            power.LogFails = true;
            var settings = new EngineSettings { Filename = file.Filename };
            settings.CheckpointStage = stage => { if (stage == "data-page") throw new IOException("the writer dies mid-checkpoint"); };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["text"] = new string('x', 5000) });
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("the writer dies mid-checkpoint"); // its header journal reached the OS cache only
            }
            // That checkpoint's header write, torn in the OS cache.
            using (var data = new FileStream(file.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                data.Position = 200;
                data.Write(Enumerable.Repeat((byte)0xA5, 3000).ToArray(), 0, 3000);
            }

            power.DataFails = power.LogFails = false; // the storage syncs again
            (byte[] Data, byte[] Log) image = default;
            var hook = NativeFileSync.SimulateErrno;
            var log = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            NativeFileSync.SimulateErrno = path =>
            {
                // A power loss just before the open's first log sync.
                if (image.Data == null && string.Equals(Path.GetFullPath(path), log, StringComparison.OrdinalIgnoreCase)) image = power.Capture();
                return hook(path);
            };
            try
            {
                using var reopened = new LiteDatabase(file.Filename);
                Values(reopened).Should().Equal(new[] { 5 }, "the open restores the header from its journal");
            }
            finally { NativeFileSync.SimulateErrno = hook; }

            image.Data.Should().NotBeNull("the open synced the log");
            FilePowerLossModel.Open(image, Values).Should().Equal(new[] { 5 }, "value 5 was acknowledged durable");
            File.Delete(FileHelper.GetLogFile(file.Filename));
        }

        /// <summary>
        /// The same with encryption: creating the encrypted data writer syncs the data file, so the
        /// open created it before the journal sync and made the torn header durable first. The log
        /// stops syncing once both writers exist (an encrypted writable open needs syncs); the tear
        /// lands in page 0's ciphertext (physical page 1).
        /// </summary>
        [Fact]
        public void Encrypted_open_repairing_a_torn_header_makes_its_journal_durable_first()
        {
            using var file = new TempFile();
            var connection = $"Filename={file.Filename};Password=secret";
            using (var setup = new LiteDatabase(connection))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            }
            using var power = new FilePowerLossModel(file.Filename);
            using (var db = new LiteDatabase(connection))
            {
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++)
                    db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));
            }

            var settings = new EngineSettings { Filename = file.Filename, Password = "secret" };
            settings.CheckpointStage = stage => { if (stage == "data-page") throw new IOException("the writer dies mid-checkpoint"); };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["text"] = new string('x', 5000) });
                power.LogFails = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("the writer dies mid-checkpoint");
            }
            using (var data = new FileStream(file.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                data.Position = Constants.PAGE_SIZE + 208;
                data.Write(Enumerable.Repeat((byte)0xA5, 3008).ToArray(), 0, 3008);
            }

            power.DataFails = power.LogFails = false;
            (byte[] Data, byte[] Log) image = default;
            var hook = NativeFileSync.SimulateErrno;
            var log = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            NativeFileSync.SimulateErrno = path =>
            {
                if (image.Data == null && string.Equals(Path.GetFullPath(path), log, StringComparison.OrdinalIgnoreCase)) image = power.Capture();
                return hook(path);
            };
            try
            {
                using var reopened = new LiteDatabase(connection);
                Values(reopened).Should().Equal(new[] { 5 }, "the open restores the header from its journal");
            }
            finally { NativeFileSync.SimulateErrno = hook; }

            image.Data.Should().NotBeNull("the open synced the log");
            FilePowerLossModel.Open(image, Values, "secret").Should().Equal(new[] { 5 }, "value 5 was acknowledged durable");
            File.Delete(FileHelper.GetLogFile(file.Filename));
        }

        private const int Rows = 64;

        /// <summary>
        /// Commits 1..5 acknowledged durable, then a full checkpoint and a commit while neither file
        /// syncs. The backfill and the WAL truncation reached the OS cache only; the checkpoint now
        /// finds the data file cannot sync before it writes, and keeps the WAL.
        /// </summary>
        private static FilePowerLossModel TruncatedWhileNothingSynced(string filename)
        {
            using (var setup = new LiteDatabase(filename))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            }
            var power = new FilePowerLossModel(filename);
            using (var db = new LiteDatabase(filename))
            {
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++)
                {
                    db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));
                    db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().BeTrue();
                }
            }
            power.DataFails = power.LogFails = true;
            using (var db = new LiteDatabase(filename))
            {
                db.Checkpoint();
                db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1 }); // keeps a WAL file
            }
            return power;
        }

        private static int[] Values(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Distinct().ToArray();
    }
}
#endif
