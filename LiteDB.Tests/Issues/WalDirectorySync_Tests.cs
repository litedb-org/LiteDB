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

        [Fact]
        public void A_directory_that_cannot_sync_reports_the_log_as_not_durable_and_keeps_committing()
        {
            using var file = new TempFile();
            var syncs = 0;
            NativeFileSync.SimulateDirectoryErrno = _ => { syncs++; return EINVAL; };
            try
            {
                using (var db = Open(file.Filename))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1 });
                    rows.Insert(new BsonDocument { ["_id"] = 2 });
                    syncs.Should().Be(1, "a directory that cannot sync is not asked again");
                    DurableLogFlush(db).Should().BeFalse("the WAL's name is not claimed durable");
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = Open(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(2);
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

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;
    }
}
#endif
