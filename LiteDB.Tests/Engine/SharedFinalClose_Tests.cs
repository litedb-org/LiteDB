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
    /// Lazy close checkpoints leave a small WAL to the connection's final close. When that
    /// close finds the mutex owned by a read-only connection, nobody else would ever
    /// checkpoint it: a read-only connection cannot. The final close must not give up at once.
    /// </summary>
    public class SharedFinalClose_Tests : IDisposable
    {
        private readonly OpenReaders _open = new OpenReaders();
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-final-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedFinalClose_Tests() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            _open.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void A_writer_closing_while_a_read_only_connection_owns_the_mutex_still_checkpoints()
        {
            using (var seed = new LiteDatabase(new ConnectionString { Filename = this.Filename, Connection = ConnectionType.Direct }))
                seed.GetCollection("docs").Insert(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["p"] = new string('p', 200) }));

            var writer = new SharedEngine(new EngineSettings { Filename = this.Filename });
            writer.Update("docs", new[] { new BsonDocument { ["_id"] = 1, ["p"] = "changed" } }).Should().Be(1);
            var log = FileHelper.GetLogFile(this.Filename);
            new FileInfo(log).Length.Should().BeGreaterThan(0, "a small write leaves its WAL to the final close");

            // A read-only connection without reader leases streams under the mutex.
            using var reader = new SharedEngine(new EngineSettings
            {
                Filename = this.Filename,
                ReadOnly = true,
                SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("registry denied")
            });
            var cursor = _open.Track(reader.Query("docs", new Query()));
            cursor.Read().Should().BeTrue();

            var closed = new Thread(writer.Dispose) { IsBackground = true };
            closed.Start();
            Thread.Sleep(200);
            cursor.Dispose();
            closed.Join(Prompt).Should().BeTrue();
            reader.Dispose();

            (File.Exists(log) && new FileInfo(log).Length > 0).Should().BeFalse("the data file alone is the database after every connection closed");
            using var check = new LiteDatabase(new ConnectionString { Filename = this.Filename, Connection = ConnectionType.Direct });
            check.GetCollection("docs").FindById(1)["p"].AsString.Should().Be("changed");
        }
    }
}
