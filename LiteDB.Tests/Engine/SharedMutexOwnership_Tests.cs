using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// #3006: a shared connection's mutex ownership does not belong to the thread that
    /// acquired it. A reader that streams under the mutex (no lease, for example an
    /// unusable reader registry) or a whole connection may be disposed on any thread,
    /// and an owner thread that exits cannot keep other threads or processes out.
    /// </summary>
    public class SharedMutexOwnership_Tests : IDisposable
    {
        private const int Count = 200;
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-owner-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedMutexOwnership_Tests()
        {
            Directory.CreateDirectory(_directory);
            using var seed = new SharedEngine(new EngineSettings { Filename = this.Filename });
            seed.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
        }

        private static BsonDocument Doc(int id, int value) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 200) };

        /// <summary>No reader lease can be registered, so readers stream under the mutex, as before v13.</summary>
        private SharedEngine OpenUnleased() => new SharedEngine(new EngineSettings
        {
            Filename = this.Filename,
            SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("registry denied")
        });

        [Fact]
        public void Unleased_reader_disposed_on_another_thread_releases_the_mutex()
        {
            using var engine = this.OpenUnleased();
            using var finish = new ManualResetEventSlim();
            var reader = ReadOnIdleThread(engine, finish, out var owner);

            RunPromptly(reader.Dispose);
            this.WriteFromAnotherInstance();
            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
            engine.Query("docs", new Query()).ToEnumerable().Count().Should().Be(Count);
        }

        [Fact]
        public async Task Unleased_reader_disposed_after_an_await_releases_the_mutex()
        {
            using var engine = this.OpenUnleased();
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();

            await Task.Run(reader.Dispose);

            this.WriteFromAnotherInstance();
            engine.Update("docs", new[] { Doc(1, 1) }).Should().Be(1);
        }

        [Fact]
        public void Dispose_on_another_thread_with_an_open_unleased_reader_releases_the_mutex()
        {
            var engine = this.OpenUnleased();
            using var finish = new ManualResetEventSlim();
            var reader = ReadOnIdleThread(engine, finish, out var owner);

            RunPromptly(engine.Dispose);
            this.WriteFromAnotherInstance();

            // The reader of the ended ownership may still be disposed, on any thread.
            RunPromptly(reader.Dispose);
            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
        }

        [Fact]
        public void Unleased_reader_owner_that_exits_does_not_keep_the_mutex()
        {
            using var engine = this.OpenUnleased();
            var reader = ReadOnIdleThread(engine, finish: null, out var owner);
            owner.Join(Prompt).Should().BeTrue();

            this.WriteFromAnotherInstance();
            engine.Update("docs", new[] { Doc(2, 2) }).Should().Be(1);
            // The exited owner's reader is released without disturbing later users.
            RunPromptly(reader.Dispose);
            engine.Query("docs", Query.All()).ToEnumerable().Should().Contain(doc => doc["value"].AsInt32 == 2);
        }

        [Fact]
        public void Transaction_owner_that_exits_lets_other_connections_write_and_is_reported()
        {
            using var engine = this.OpenUnleased();
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var owner = new Thread(() =>
            {
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("docs").Update(Doc(1, 99));
            });
            owner.Start();
            owner.Join(Prompt).Should().BeTrue();

            // The exited owner's engine is closed before the mutex is released, so the
            // other connection neither waits nor meets its open file handles.
            this.WriteFromAnotherInstance();

            Action next = () => db.GetCollection("docs").Count();
            next.Should().Throw<LiteException>().WithMessage("*owner thread exited*");
            db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(0, "the exited owner never committed");
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task Other_process_waits_only_while_an_unleased_reader_is_open()
        {
            using var engine = this.OpenUnleased();
            // A thread that owns the mutex and exits releases it, as the OS abandons
            // it, so the reader's thread must outlive the awaits below; a test runner
            // worker may not.
            using var finish = new ManualResetEventSlim();
            var reader = ReadOnIdleThread(engine, finish, out var owner);

            using var writer = new LiteDB.Internals.MvccProcess("insert", this.Filename, null, "1000");
            var done = writer.Expect("done");
            (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(1)))).Should().NotBeSameAs(done,
                "the open reader owns the mutex");

            await Task.Run(reader.Dispose);
            await done;
            await writer.Finish();
            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
            engine.Query("docs", new Query()).ToEnumerable().Count().Should().Be(Count + 20);
        }
#endif

#if DEBUG || TESTING
        /// <summary>
        /// The owner starts another operation while its unleased reader is open: OpenDatabase
        /// sees the open engine and does not reopen it. A reader disposed on another thread
        /// at that moment must not close the engine before the operation is counted.
        /// </summary>
        [Fact]
        public void Reader_disposed_on_another_thread_cannot_close_the_engine_under_a_starting_operation()
        {
            using var engine = this.OpenUnleased();
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();

            var starter = Thread.CurrentThread;
            using var inWindow = new ManualResetEventSlim();
            using var disposed = new ManualResetEventSlim();
            Exception disposeError = null;
            var disposer = new Thread(() =>
            {
                inWindow.Wait();
                try { reader.Dispose(); }
                catch (Exception ex) { disposeError = ex; }
                disposed.Set();
            }) { IsBackground = true };
            disposer.Start();

            engine.BeforeCountingUser = () =>
            {
                if (!ReferenceEquals(Thread.CurrentThread, starter) || inWindow.IsSet) return;
                inWindow.Set();
                // Without the fix the disposal completes here; with it, it waits for the count.
                disposed.Wait(TimeSpan.FromMilliseconds(500));
            };
            try
            {
                engine.Insert("other", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32).Should().Be(1);
            }
            finally
            {
                engine.BeforeCountingUser = null;
                disposer.Join(Prompt).Should().BeTrue();
            }
            disposeError.Should().BeNull();
            engine.Query("other", new Query()).ToEnumerable().Count().Should().Be(1);
            this.WriteFromAnotherInstance();
        }
#endif

        /// <summary>Opens a reader that streams under the mutex, on a thread that then idles.</summary>
        private static IBsonDataReader ReadOnIdleThread(SharedEngine engine, ManualResetEventSlim finish, out Thread owner)
        {
            IBsonDataReader reader = null;
            Exception error = null;
            using var opened = new ManualResetEventSlim();
            owner = new Thread(() =>
            {
                try
                {
                    reader = engine.Query("docs", new Query());
                    reader.Read().Should().BeTrue();
                }
                catch (Exception ex) { error = ex; }
                opened.Set();
                finish?.Wait();
            }) { IsBackground = true };
            owner.Start();
            opened.Wait(Prompt).Should().BeTrue();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            return reader;
        }

        private void WriteFromAnotherInstance()
        {
            // Another instance shares only the named mutex, like another process.
            RunPromptly(() =>
            {
                using var other = new SharedEngine(new EngineSettings { Filename = this.Filename });
                other.Insert("probe", new[] { new BsonDocument { ["_id"] = Guid.NewGuid() } }, BsonAutoId.Int32);
            });
        }

        private static void RunPromptly(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.Start();
            thread.Join(Prompt).Should().BeTrue("mutex ownership must not wait for a particular thread");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
