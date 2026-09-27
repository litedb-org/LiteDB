using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// #3004: a shared operation's engine closes without checkpoint until the WAL reaches
    /// <see cref="SharedEngine.CLOSE_CHECKPOINT_PAGES"/> (at most the CHECKPOINT pragma).
    /// The connection's final close still checkpoints, so the data file alone is the
    /// database once every connection closed, and the WAL stays authoritative meanwhile.
    /// </summary>
    public class SharedLazyCheckpoint_Tests : IDisposable
    {
        private const string Reclaimed = "after-reclaim";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-lazy-" + Guid.NewGuid().ToString("N"));
        private int _reclaims;

        public SharedLazyCheckpoint_Tests() => Directory.CreateDirectory(_directory);

        private string Filename => Path.Combine(_directory, "test.db");

        private string LogFilename => FileHelper.GetLogFile(this.Filename);

        private long LogLength => File.Exists(this.LogFilename) ? new FileInfo(this.LogFilename).Length : 0;

        private SharedEngine Open(string password, bool readOnly = false) => new SharedEngine(new EngineSettings
        {
            Filename = this.Filename,
            Password = password,
            ReadOnly = readOnly,
            CheckpointStage = stage =>
            {
                if (stage == Reclaimed) Interlocked.Increment(ref _reclaims);
            }
        });

        private static BsonDocument Doc(int id) =>
            new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 3000) };

        private static void Insert(ILiteEngine engine, int id) =>
            engine.Insert("docs", new[] { Doc(id) }, BsonAutoId.Int32);

        private void AssertDatabase(string password, int count)
        {
            using (var db = new LiteDatabase(new ConnectionString { Filename = this.Filename, Password = password }))
            {
                db.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Operations_below_the_threshold_leave_the_wal_and_dispose_checkpoints_it(string password)
        {
            using (var engine = this.Open(password))
            {
                for (var id = 1; id <= 5; id++) Insert(engine, id);

                _reclaims.Should().Be(0, "five small operations stay below the close threshold");
                this.LogLength.Should().BeGreaterThan(0, "the committed operations remain in the authoritative WAL");
                new LiteDatabase(engine, disposeOnClose: false).GetCollection("docs").Count().Should().Be(5);
            }

            _reclaims.Should().Be(1, "the connection's final close checkpoints");
            File.Exists(this.LogFilename).Should().BeFalse();
            this.AssertDatabase(password, 5);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void A_wal_past_the_threshold_is_checkpointed_by_the_next_close(string password)
        {
            using (var engine = this.Open(password))
            {
                // Each insert writes between two and six pages, so the threshold is
                // reached after threshold / 6 to threshold / 2 operations.
                var below = SharedEngine.CLOSE_CHECKPOINT_PAGES / 6;
                for (var id = 1; id <= below; id++) Insert(engine, id);
                _reclaims.Should().Be(0);

                var id2 = below;
                while (_reclaims == 0 && id2 < SharedEngine.CLOSE_CHECKPOINT_PAGES) Insert(engine, ++id2);
                _reclaims.Should().Be(1, "a close past the threshold checkpoints");
                File.Exists(this.LogFilename).Should().BeFalse("with no reader the checkpoint reclaims the whole WAL");

                Insert(engine, ++id2);
                this.LogLength.Should().BeGreaterThan(0);
                _reclaims.Should().Be(1, "the next operation starts a new lazy WAL");
                new LiteDatabase(engine, disposeOnClose: false).GetCollection("docs").Count().Should().Be(id2);
            }
            File.Exists(this.LogFilename).Should().BeFalse();
        }

        [Fact]
        public void A_smaller_checkpoint_pragma_still_bounds_the_wal()
        {
            using (var engine = this.Open(null))
            {
                engine.Pragma(Pragmas.CHECKPOINT, 5);
                var before = _reclaims;
                for (var id = 1; id <= 10; id++) Insert(engine, id);
                (_reclaims - before).Should().BeGreaterOrEqualTo(2, "the close threshold never exceeds the CHECKPOINT pragma");
            }
            this.AssertDatabase(null, 10);
        }

        [Fact]
        public void A_disabled_checkpoint_pragma_disables_close_checkpoints_as_before()
        {
            using (var engine = this.Open(null))
            {
                engine.Pragma(Pragmas.CHECKPOINT, 0);
                for (var id = 1; id <= 60; id++) Insert(engine, id);
            }
            _reclaims.Should().Be(0, "CHECKPOINT = 0 disables automatic checkpoints, including the final close");
            this.LogLength.Should().BeGreaterThan(0);
            this.AssertDatabase(null, 60);
        }

        [Fact]
        public void Explicit_checkpoint_is_not_deferred()
        {
            using (var engine = this.Open(null))
            {
                for (var id = 1; id <= 3; id++) Insert(engine, id);
                engine.Checkpoint();
                _reclaims.Should().Be(1);
                File.Exists(this.LogFilename).Should().BeFalse();
            }
            this.AssertDatabase(null, 3);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Two_connections_leave_no_wal_once_both_closed(bool firstClosesFirst)
        {
            var first = this.Open(null);
            var second = this.Open(null);
            try
            {
                for (var id = 1; id <= 6; id++) Insert(id % 2 == 0 ? second : first, id);
                this.LogLength.Should().BeGreaterThan(0);
                (firstClosesFirst ? first : second).Dispose();
                Insert(firstClosesFirst ? second : first, 7);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            File.Exists(this.LogFilename).Should().BeFalse();
            this.AssertDatabase(null, 7);
        }

        [Fact]
        public void A_read_only_connection_neither_checkpoints_nor_removes_the_wal()
        {
            using (var writer = this.Open(null))
            {
                for (var id = 1; id <= 4; id++) Insert(writer, id);
                var wal = TempFile.ReadAllBytesShared(this.LogFilename);
                var data = TempFile.ReadAllBytesShared(this.Filename);

                using (var reader = this.Open(null, readOnly: true))
                using (var db = new LiteDatabase(reader, disposeOnClose: false))
                {
                    db.GetCollection("docs").Count().Should().Be(4);
                }

                TempFile.ReadAllBytesShared(this.LogFilename).Should().Equal(wal);
                TempFile.ReadAllBytesShared(this.Filename).Should().Equal(data);
                _reclaims.Should().Be(0);
            }
            File.Exists(this.LogFilename).Should().BeFalse("the writer's final close checkpoints");
            this.AssertDatabase(null, 4);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void A_crash_image_of_the_lazy_wal_recovers_every_operation(string password)
        {
            var image = Path.Combine(_directory, "image.db");
            using (var engine = this.Open(password))
            {
                for (var id = 1; id <= 5; id++) Insert(engine, id);
                // A killed process keeps both files as they are between operations.
                File.Copy(this.Filename, image);
                File.Copy(this.LogFilename, FileHelper.GetLogFile(image));
            }

            using (var db = new LiteDatabase(new ConnectionString { Filename = image, Password = password }))
            {
                db.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 5));
            }
            File.Exists(FileHelper.GetLogFile(image)).Should().BeFalse();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
