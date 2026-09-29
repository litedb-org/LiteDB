#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// Syncing a new WAL's contents does not persist its name on Unix. A lazy close (or direct
    /// mode between checkpoints) leaves the WAL as the only copy of acknowledged commits, so its
    /// directory entry must be durable before the first commit that depends on it returns.
    /// The directory sync is injected, so these run on every platform.
    /// </summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class WalDirectorySync_Tests
    {
        private const int EIO = 5;
        private const int EINVAL = 22;

        [Fact]
        public void A_wal_is_published_in_its_directory_before_the_first_commit_returns()
        {
            using var file = new TempFile();
            var syncs = new List<string>();
            NativeFileSync.SimulateDirectoryErrno = directory => { syncs.Add(directory); return 0; };
            try
            {
                using (var db = Open(file.Filename))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1 });
                    syncs.Should().HaveCount(1, "the first durable commit publishes the new WAL's name");
                    syncs[0].Should().Be(Path.GetDirectoryName(Path.GetFullPath(file.Filename)));

                    rows.Insert(new BsonDocument { ["_id"] = 2 });
                    db.Checkpoint();
                    rows.Insert(new BsonDocument { ["_id"] = 3 });
                    syncs.Should().HaveCount(1, "the name stays durable while this engine keeps the WAL");
                }

                // The closed engine deleted its empty WAL; the next one creates a new file.
                using (var db = Open(file.Filename))
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4 });
                    syncs.Should().HaveCount(2);
                    DurableLogFlush(db).Should().BeTrue();
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }
        }

        [Fact]
        public void Commits_that_opted_out_of_durability_do_not_sync_the_directory()
        {
            using var file = new TempFile();
            var syncs = 0;
            NativeFileSync.SimulateDirectoryErrno = _ => { syncs++; return 0; };
            try
            {
                using var db = Open(file.Filename, durableCommits: false);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                syncs.Should().Be(0);
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }
        }

        /// <summary>
        /// Decision 9 of docs/decisions/durability-policy.md: a WAL directory that answers "cannot sync"
        /// (#2242) fails a durable commit loudly, before it writes. The data file stays byte for byte,
        /// the log holds no frame, reads work, $database reports the failure, and the next write throws
        /// with it without asking the directory again (decision 6).
        /// </summary>
        [Fact]
        public void A_directory_that_cannot_sync_fails_a_durable_commit_before_it_writes()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var syncs = 0;
            NativeFileSync.SimulateDirectoryErrno = _ => { syncs++; return EINVAL; };
            try
            {
                using (var db = Open(file.Filename))
                {
                    var rows = db.GetCollection("rows");
                    var data = TempFile.ReadAllBytesShared(file.Filename);
                    Action insert = () => rows.Insert(new BsonDocument { ["_id"] = 1 });
                    insert.Should().Throw<IOException>().WithMessage(WriteFailureAssert.DirectoryNotWritten + "*");
                    syncs.Should().Be(1);
                    TempFile.ReadAllBytesShared(file.Filename).Should().Equal(data);
                    LogLength(logName).Should().Be(0, "no frame was written");

                    rows.Count().Should().Be(0);
                    var reason = WriteFailureAssert.CommitRefused(db, WriteFailureAssert.DirectoryNotWritten);
                    WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 2 }), reason);
                    syncs.Should().Be(1, "a directory that cannot sync is not asked again");
                    TempFile.ReadAllBytesShared(file.Filename).Should().Equal(data);
                    LogLength(logName).Should().Be(0);
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = Open(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(0);
            reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        /// <summary>
        /// Opted out of durable commits, the same directory commits and checkpoints as 5.0.21 did
        /// (decision 9, proposed default A): "cannot sync" is not a failure, and $database reports
        /// that the log is not durable.
        /// </summary>
        [Fact]
        public void A_directory_that_cannot_sync_keeps_committing_without_durable_commits()
        {
            using var file = new TempFile();
            var syncs = 0;
            NativeFileSync.SimulateDirectoryErrno = _ => { syncs++; return EINVAL; };
            try
            {
                using (var db = Open(file.Filename, durableCommits: false))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1 });
                    rows.Insert(new BsonDocument { ["_id"] = 2 });
                    db.Checkpoint();
                    rows.Insert(new BsonDocument { ["_id"] = 3 });
                    syncs.Should().Be(1, "a directory that cannot sync is not asked again");
                    DurableLogFlush(db).Should().BeFalse("the WAL's name is not claimed durable");
                    WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = Open(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3);
        }

        [Fact]
        public void A_failed_directory_sync_fails_the_commit()
        {
            using var file = new TempFile();
            NativeFileSync.SimulateDirectoryErrno = _ => EIO;
            try
            {
                using var db = Open(file.Filename);
                Action insert = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                insert.Should().Throw<Exception>("an unpublished WAL name must not be acknowledged");
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }
        }

        private static LiteDatabase Open(string filename, bool durableCommits = true) =>
            new LiteDatabase(new LiteEngine(new EngineSettings { Filename = filename, DurableCommits = durableCommits }));

        private static long LogLength(string logName) => File.Exists(logName) ? new FileInfo(logName).Length : 0;

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;
    }
}
#endif
