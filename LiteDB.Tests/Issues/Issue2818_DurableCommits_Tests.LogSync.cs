using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// Checkpoints on log storage that cannot sync (#2242) or whose sync fails. Without durable
    /// commits "cannot sync" is not a failure (proposed default A of docs/decisions/durability-policy.md):
    /// checkpoints proceed in write order. With durable commits it fails loudly (decision 3), and any
    /// failure is sticky (decision 6): reads keep working and every later write throws with the record.
    /// </summary>
    public partial class Issue2818_DurableCommits_Tests
    {
        // Plain files only: opening an encrypted WAL stream syncs its preamble, which
        // such storage already rejected before #2818.
        [Fact]
        public void Checkpoint_tolerates_log_storage_that_cannot_sync_without_durable_commits()
        {
            const string password = null;
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits: false, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                CommitFourTransactions(db, rows);
                storage.Log.DurableFailure = new UnauthorizedAccessException("Access to the path is denied.");

                db.Checkpoint();
                rows.Insert(new BsonDocument { ["_id"] = 100 });
                db.Checkpoint();

                AssertCommitted(rows, 100);
                DurableLogFlush(db).Should().BeFalse("the weaker guarantee must be discoverable");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            }

            // The storage still cannot sync: reopening recovers and keeps writing (#2242).
            using (var engine = storage.Open(durableCommits: false, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = db.GetCollection("rows");
                AssertCommitted(rows, 100);
                rows.Insert(new BsonDocument { ["_id"] = 101 });
                db.Checkpoint();
                DurableLogFlush(db).Should().BeFalse();
                WriteFailureAssert.NoneRecorded(db);
            }

            storage.Log.DurableFailure = null;
            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            AssertCommitted(reopened.GetCollection("rows"), 100, 101);
            DurableLogFlush(reopened).Should().BeTrue("storage that syncs again regains durable commits");
        }

        /// <summary>
        /// With durable commits the checkpoint's log barrier finds that the log cannot sync: an explicit
        /// checkpoint is the caller's own operation, so it throws (decision 3, implementation note 5)
        /// before it overwrites a data page. The data file stays byte for byte, reads return exactly
        /// the committed rows, $database reports the failure, and the next write throws with it.
        /// </summary>
        [Fact]
        public void Checkpoint_on_log_storage_that_cannot_sync_fails_loudly_with_durable_commits()
        {
            const string password = null;
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits: true, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                CommitFourTransactions(db, rows);
                var data = ReadShared(storage.Data.Name);
                var rejection = new UnauthorizedAccessException("Access to the path is denied.");
                storage.Log.DurableFailure = rejection;

                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage(WriteFailureAssert.LogCannotSync + "*")
                    .Which.InnerException.Should().BeSameAs(rejection);
                ReadShared(storage.Data.Name).Should().Equal(data, "no data page is overwritten before the log synced");

                AssertCommitted(rows);
                var reason = WriteFailureAssert.Recorded(db, "A checkpoint", "log", WriteFailureAssert.LogCannotSync, walKept: true);
                var log = ReadShared(storage.Log.Name);
                var syncs = storage.Log.DurableFlushes;
                WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 100 }), reason);
                storage.Log.DurableFlushes.Should().Be(syncs, "a refused write asks the storage nothing");
                ReadShared(storage.Data.Name).Should().Equal(data);
                ReadShared(storage.Log.Name).Should().Equal(log);
                AssertCommitted(rows);
            }

            storage.Log.DurableFailure = null;
            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            WriteFailureAssert.NoneRecorded(reopened, "a reopen retries");
            AssertCommitted(reopened.GetCollection("rows"));
            reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 100 });
            reopened.Checkpoint();
            AssertCommitted(reopened.GetCollection("rows"), 100);
            DurableLogFlush(reopened).Should().BeTrue();
        }

        [Fact]
        public void Automatic_checkpoint_tolerates_log_storage_that_stops_syncing_without_durable_commits()
        {
            const string password = null;
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits: false, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                db.CheckpointSize = 1;
                db.Checkpoint();
                storage.Log.DurableFailure = new UnauthorizedAccessException("Access to the path is denied.");

                // Each commit reaches the automatic checkpoint limit.
                for (var i = 1; i <= 3; i++) rows.Insert(new BsonDocument { ["_id"] = i });

                rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(-1, 0, 1, 2, 3);
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            }

            using var reopenedEngine = storage.Open(durableCommits: false, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(-1, 0, 1, 2, 3);
        }

        /// <summary>
        /// With durable commits a log that stops syncing after the engine's proof fails the commit that
        /// finds out, with an unknown outcome (implementation note 4): its frames reached the operating
        /// system. The automatic checkpoint behind it never runs, so the data file stays byte for byte;
        /// reads keep working from the files as they are, and the next write throws with the recorded
        /// failure. A later engine lets the files decide: the commit's frames reached them.
        /// </summary>
        [Fact]
        public void Commit_on_a_log_that_stops_syncing_fails_before_its_automatic_checkpoint_with_durable_commits()
        {
            const string password = null;
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits: true, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                db.CheckpointSize = 1;
                db.Checkpoint();
                var data = ReadShared(storage.Data.Name);
                storage.Log.DurableFailure = new UnauthorizedAccessException("Access to the path is denied.");

                Action insert = () => rows.Insert(new BsonDocument { ["_id"] = 1 });
                insert.Should().Throw<IOException>().WithMessage(WriteFailureAssert.OutcomeUnknown + "*");
                ReadShared(storage.Data.Name).Should().Equal(data, "the automatic checkpoint did not run");

                // The read-only reopen replays the files as they are: the failed commit's frames reached them,
                // so it shows (its outcome is unknown). Showing only what was acknowledged is a later layer's.
                rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(-1, 0, 1);
                var reason = WriteFailureAssert.Recorded(db, "A commit's log flush", "log", WriteFailureAssert.OutcomeUnknown, walKept: true);
                WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 2 }), reason);
                ReadShared(storage.Data.Name).Should().Equal(data);
            }

            storage.Log.DurableFailure = null;
            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(-1, 0, 1);
        }

        /// <summary>
        /// A failed log sync (not "cannot sync") is a failure in both modes: the checkpoint throws it
        /// before it overwrites a data page. It is sticky (decision 6): reads keep returning exactly the
        /// committed rows, $database reports it, and the next write throws with it; a reopen retries.
        /// </summary>
        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "secret")]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public void Checkpoint_stops_before_overwriting_data_when_the_log_sync_fails(bool durableCommits, string password)
        {
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                CommitFourTransactions(db, rows);
                var before = ReadShared(storage.Data.Name);
                // A failed sync, unlike an unsupported one, leaves the redo state unknown.
                storage.Log.DurableFailure = new IOException("The request could not be performed because of an I/O device error.");

                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().Which.Should().BeSameAs(storage.Log.DurableFailure);
                ReadShared(storage.Data.Name).Should().Equal(before);

                AssertCommitted(rows);
                var reason = WriteFailureAssert.Recorded(db, "A checkpoint", "log", storage.Log.DurableFailure.Message, walKept: true);
                WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 100 }), reason);
                ReadShared(storage.Data.Name).Should().Equal(before);
                AssertCommitted(rows);
            }

            storage.Log.DurableFailure = null;
            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            AssertCommitted(reopened.GetCollection("rows"));
            reopened.Checkpoint();
        }

        // Exactly the rows WarmUp and CommitFourTransactions committed, and the given ids.
        private static void AssertCommitted(ILiteCollection<BsonDocument> rows, params int[] more)
        {
            var all = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToList();
            all.Select(x => x["_id"].AsInt32).Should().Equal(new[] { -1, 0, 1, 2, 3, 4 }.Concat(more).OrderBy(x => x));
            all.Where(x => x["_id"].AsInt32 == 2).Should().OnlyContain(x => x["value"].AsString == "updated");
            all.Where(x => x["_id"].AsInt32 != 2).Should().OnlyContain(x => !x.ContainsKey("value"));
        }
    }
}
