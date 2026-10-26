using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: for a writable non-MemoryStream data stream without a log stream,
    /// <c>new LiteDatabase(stream)</c> persistently sets the CHECKPOINT pragma to 1 and Dispose
    /// persistently restores it through <c>_engine.Pragma(...)</c> before <c>_engine.Dispose()</c>
    /// (bf3987fbc, #2652). 5.0.21's Dispose only called <c>_engine.Dispose()</c>.
    /// The forced checkpoint fixes a real 5.0.21 data loss (commits stayed in the in-memory WAL);
    /// the regressions are its side effects. Checked with the LiteDB 5.0.21 package: a second
    /// Dispose and a Dispose with an open transaction do not throw, and CHECKPOINT stays 1000
    /// (5.0.21 did lose the committed rows in the last two scenarios, which HEAD fixes).
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class StreamDatabaseDispose_Tests
    {
        [Fact]
        public void Second_dispose_does_not_throw()
        {
            using var file = new TempFile();
            using var stream = new FileStream(file.Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            var db = new LiteDatabase(stream);
            db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
            db.Dispose();

            // HEAD: Pragma getter validates the disposed engine and throws ENGINE_DISPOSED.
            db.Invoking(x => x.Dispose()).Should().NotThrow();
        }

        [Fact]
        public void Dispose_with_an_open_transaction_rolls_back_and_releases_the_file()
        {
            using var file = new TempFile();

            using (var stream = new FileStream(file.Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                var db = new LiteDatabase(stream);
                db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 2 });

                // Typical `using (db) { db.BeginTrans(); ...; throw; }` path.
                // HEAD: Pragma(CHECKPOINT, x) throws AlreadyExistsTransaction and
                // _engine.Dispose() (rollback, stream release) never runs.
                db.Invoking(x => x.Dispose()).Should().NotThrow();
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                ((object)db.GetCollection("items").FindById(2)).Should().BeNull("the uncommitted insert must be rolled back");
                ((object)db.GetCollection("items").FindById(1)).Should().NotBeNull();
            }
        }

        [Fact]
        public void Opening_through_a_stream_does_not_persistently_change_the_checkpoint_pragma()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
                db.CheckpointSize.Should().Be(1000);
            }

            // Stream-based use that ends without LiteDatabase.Dispose (process exit/crash,
            // or the caller only disposes its own stream).
            var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite);
            var abandoned = new LiteDatabase(stream);
            abandoned.GetCollection("items").Insert(new BsonDocument { ["_id"] = 2 });
            stream.Dispose();
            GC.SuppressFinalize(abandoned);

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("items").Count().Should().Be(2);
                // HEAD: 1 is now stored in the header; every later filename open checkpoints
                // (and syncs the data file) on every commit.
                db.CheckpointSize.Should().Be(1000);
            }
        }
    }
}
