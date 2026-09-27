using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A partial checkpoint under a live shared lease validates and scans the whole
    /// WAL, which that lease keeps growing. Commits and engine closes must share the
    /// auto-checkpoint back-off instead of each paying for one; otherwise a write loop
    /// under a long-lived reader is quadratic. The back-off clock is frozen, so every
    /// partial checkpoint after the first claimed attempt would be an unrationed one.
    /// </summary>
    public class SharedCheckpointRationing_Tests : IDisposable
    {
        private const int Count = 120;
        private const string PartialReclaim = "wal-slots-flushed";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-ration-" + Guid.NewGuid().ToString("N"));
        private int _partialReclaims;

        public SharedCheckpointRationing_Tests() => Directory.CreateDirectory(_directory);

        private string Filename => Path.Combine(_directory, "test.db");

        private static BsonDocument Doc(int id, int value) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 200) };

        private SharedEngine Open(string password)
        {
            var engine = new SharedEngine(new EngineSettings
            {
                Filename = this.Filename,
                Password = password,
                CheckpointStage = stage =>
                {
                    if (stage == PartialReclaim) Interlocked.Increment(ref _partialReclaims);
                }
            })
            {
                PinIdleLimit = TimeSpan.FromMinutes(10),
                PinHoldLimit = TimeSpan.FromMinutes(10)
            };
            var settings = (EngineSettings)typeof(SharedEngine)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            settings.CheckpointBackoff.Timestamp = () => 1;
            return engine;
        }

        private void Seed(SharedEngine engine)
        {
            engine.Pragma(Pragmas.CHECKPOINT, 1);
            engine.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
            Interlocked.Exchange(ref _partialReclaims, 0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Writes_while_iterating_ration_partial_checkpoints(string password)
        {
            using (var engine = this.Open(password))
            using (var db = new LiteDatabase(engine))
            {
                this.Seed(engine);
                var col = db.GetCollection("docs");
                foreach (var doc in col.FindAll())
                {
                    doc["value"] = 1;
                    col.Update(doc);
                }
                _partialReclaims.Should().BeLessOrEqualTo(1, "with the back-off clock frozen only the first commit may pay for partial work");
                col.Count(Query.EQ("value", 1)).Should().Be(Count);
            }
            File.Exists(FileHelper.GetLogFile(this.Filename)).Should().BeFalse("rationing must not stop the final reclaiming checkpoint");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Closes_under_another_instance_lease_ration_partial_checkpoints(string password)
        {
            using (var writer = this.Open(password))
            using (var readers = this.Open(password))
            {
                this.Seed(writer);
                using (var reader = readers.Query("docs", new Query()))
                {
                    reader.Read().Should().BeTrue();
                    for (var id = 1; id <= Count; id++) writer.Update("docs", new[] { Doc(id, 1) });
                    _partialReclaims.Should().BeLessOrEqualTo(1, "each write opens and closes an engine; its close must follow the back-off");
                    var seen = 1;
                    while (reader.Read()) seen++;
                    seen.Should().Be(Count, "the reader keeps its snapshot");
                }
                writer.Query("docs", new Query { Where = { BsonExpression.Create("$.value = 1") } }).ToEnumerable().Count().Should().Be(Count);
            }
            File.Exists(FileHelper.GetLogFile(this.Filename)).Should().BeFalse("closing without leases reclaims inside the back-off window");
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
