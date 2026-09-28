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
    /// acknowledged commits stay recoverable. Nor does it write a header journal the log cannot
    /// sync (decision D; external review, point 1), so the torn-header tests build that image
    /// directly: a journal in the log file that never synced, next to a torn header.
    /// A commit or checkpoint on a log that cannot sync now fails loudly with durable commits
    /// (decision 3 of docs/decisions/durability-policy.md), so the engines that write while it
    /// cannot sync opted out ("durable commits=false"): "cannot sync" is not a failure for them
    /// (proposed default A), and they reach the same files as before.
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
        /// header durable while the journal, its only repair, was still in the OS cache. LiteDB no
        /// longer leaves such a journal: a checkpoint whose log cannot sync refuses before it writes
        /// one, in both modes (decision D; external review, point 1). The image is built directly (see
        /// <see cref="CheckpointBehindAJournalThatNeverSynced"/>), as an engine before that change left
        /// it with durable commits off on a log that could not sync.
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

            CheckpointBehindAJournalThatNeverSynced(power, new EngineSettings { Filename = file.Filename }, password: null);
            // That checkpoint's header write, torn in the OS cache.
            using (var data = new FileStream(file.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                data.Position = 200;
                data.Write(Enumerable.Repeat((byte)0xA5, 3000).ToArray(), 0, 3000);
            }

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
        /// open created it before the journal sync and made the torn header durable first. The tear
        /// lands in page 0's ciphertext (physical page 1). The journal that never synced is built as
        /// above: LiteDB no longer writes one (decision D).
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

            CheckpointBehindAJournalThatNeverSynced(power, new EngineSettings { Filename = file.Filename, Password = "secret" }, "secret");
            using (var data = new FileStream(file.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                data.Position = Constants.PAGE_SIZE + 208;
                data.Write(Enumerable.Repeat((byte)0xA5, 3008).ToArray(), 0, 3008);
            }

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
        /// A writer that commits, then dies in its checkpoint's backfill behind a header journal that is
        /// in the log file but never reached the device. Its commit syncs as usual. The checkpoint's log
        /// syncs answer success, so it writes its journal and starts the backfill, but the power-loss
        /// model keeps the log as of before them: its image is the one taken before the journal's sync.
        /// The data file syncs right before the journal, as every checkpoint's does.
        /// </summary>
        private static void CheckpointBehindAJournalThatNeverSynced(FilePowerLossModel power, EngineSettings settings, string password)
        {
            var hook = NativeFileSync.SimulateErrno;
            var logName = Path.GetFullPath(FileHelper.GetLogFile(settings.Filename));
            var checkpointing = false;
            settings.CheckpointStage = stage =>
            {
                if (stage == "before-commit-lock") checkpointing = true;
                if (stage == "data-page") throw new IOException("the writer dies mid-checkpoint");
            };
            NativeFileSync.SimulateErrno = path =>
                checkpointing && string.Equals(Path.GetFullPath(path), logName, StringComparison.OrdinalIgnoreCase) ? 0 : hook(path);
            try
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                db.CheckpointSize = 0;
                db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["text"] = new string('x', 5000) });
                var synced = power.Capture().Log;
                HasJournal(synced, password).Should().BeFalse();
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("the writer dies mid-checkpoint");
                power.Capture().Log.Should().Equal(synced, "the journal never reached the device");
                HasJournal(SyncPowerLossModel.ReadShared(logName), password).Should().BeTrue("the journal is in the log file");
            }
            finally { NativeFileSync.SimulateErrno = hook; }
        }

        private static bool HasJournal(byte[] log, string password)
        {
            using var raw = new MemoryStream(log);
            if (password == null) return HeaderJournal.Read(raw) != null;
            using var plain = new AesStream(password, raw, allowRecovery: false);
            return HeaderJournal.Read(plain) != null;
        }

        /// <summary>
        /// Commits 1..5 acknowledged durable, then a full checkpoint and a commit while neither file
        /// syncs. The backfill and the WAL truncation reached the OS cache only; the checkpoint now
        /// finds the data file cannot sync before it writes, and keeps the WAL. That engine opted out
        /// of durable commits: with them its commit on a log that cannot sync would fail loudly.
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
            using (var db = new LiteDatabase($"Filename={filename};durable commits=false"))
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
