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
    /// A shared operation ends by posting its mutex release to the connection's holder
    /// thread (#3006). The final close's checkpoint only tries the mutex, so it used to
    /// find its own release still in flight and leave the WAL behind; on Linux that
    /// happened in most tight open/close loops. The delay here makes the window certain.
    /// </summary>
    public class SharedPendingRelease_Tests : IDisposable
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-release-" + Guid.NewGuid().ToString("N"));

        public SharedPendingRelease_Tests() => Directory.CreateDirectory(_directory);

        private string Filename => Path.Combine(_directory, "test.db");

        private string LogFilename => FileHelper.GetLogFile(this.Filename);

        private SharedEngine Open(bool slowRelease)
        {
            var engine = new SharedEngine(new EngineSettings { Filename = this.Filename });
            if (slowRelease) engine.MutexOwner.BeforePostedRelease = () => Thread.Sleep(Delay);
            return engine;
        }

        private static void Insert(ILiteEngine engine, int id) =>
            engine.Insert("docs", new[] { new BsonDocument { ["_id"] = id, ["payload"] = new string('p', 3000) } }, BsonAutoId.Int32);

        [Fact]
        public void Final_close_waits_for_its_own_release_before_checkpointing()
        {
            using (var engine = this.Open(slowRelease: true))
            {
                for (var id = 1; id <= 3; id++) Insert(engine, id);
                File.Exists(this.LogFilename).Should().BeTrue("three operations stay below the close threshold");
            }

            File.Exists(this.LogFilename).Should().BeFalse("the final close checkpoints once its own release completed");
            this.Count().Should().Be(3);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_disposed_connection_holds_no_mutex_for_the_next_final_close(bool firstClosesFirst)
        {
            var first = this.Open(slowRelease: true);
            var second = this.Open(slowRelease: true);
            try
            {
                for (var id = 1; id <= 6; id++) Insert(id % 2 == 0 ? second : first, id);
                (firstClosesFirst ? first : second).Dispose();
                Insert(firstClosesFirst ? second : first, 7);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            File.Exists(this.LogFilename).Should().BeFalse();
            this.Count().Should().Be(7);
        }

        [Fact]
        public void Dispose_returns_only_after_the_mutex_is_released()
        {
            var engine = this.Open(slowRelease: true);
            Insert(engine, 1);
            engine.Dispose();

            // Another connection's final close only tries the mutex; it must find it free.
            using var mutex = engine.MutexOwner.Mutex;
            var acquired = mutex.WaitOne(0);
            if (acquired) mutex.ReleaseMutex();
            acquired.Should().BeTrue("a disposed connection must not hold the mutex, even briefly");
        }

        private int Count()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = this.Filename });
            return db.GetCollection("docs").Count();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
