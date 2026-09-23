using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A write from a thread iterating a leased reader pins the engine: a holder
    /// thread keeps the named mutex between that thread's calls. Any thread must be
    /// able to end the pin, and nothing may leave the mutex owned by a live thread
    /// that never calls back in, or every other thread and process would wait forever.
    /// </summary>
    public class SharedReaderPin_Tests : IDisposable
    {
        private const int Count = 200;
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Forever = TimeSpan.FromMinutes(10);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-pin-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string LogFilename => FileHelper.GetLogFile(this.Filename);

        public SharedReaderPin_Tests() => Directory.CreateDirectory(_directory);

        private static BsonDocument Doc(int id, int value) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 200) };

        /// <summary>
        /// Limits that never expire isolate the requested ends from the idle and
        /// total limits, which end a forgotten pin on their own.
        /// </summary>
        private SharedEngine Open(bool expire = false)
        {
            var engine = new SharedEngine(new EngineSettings { Filename = this.Filename });
            if (!expire)
            {
                engine.PinIdleLimit = Forever;
                engine.PinHoldLimit = Forever;
            }
            return engine;
        }

        private SharedEngine Seed()
        {
            var engine = this.Open();
            engine.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
            return engine;
        }

        /// <summary>Opens a reader and pins it with one write on a thread that then idles.</summary>
        private static IBsonDataReader PinOnIdleThread(SharedEngine engine, ManualResetEventSlim finish, out Thread owner)
        {
            IBsonDataReader reader = null;
            Exception error = null;
            using var pinned = new ManualResetEventSlim();
            owner = new Thread(() =>
            {
                try
                {
                    reader = engine.Query("docs", new Query());
                    reader.Read().Should().BeTrue();
                    engine.Update("docs", new[] { Doc(1, 1) });
                }
                catch (Exception ex) { error = ex; }
                pinned.Set();
                finish?.Wait();
            }) { IsBackground = true };
            owner.Start();
            pinned.Wait(Prompt).Should().BeTrue();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            return reader;
        }

        [Fact]
        public void Reader_disposed_off_the_pinning_thread_ends_the_pin_while_its_owner_idles()
        {
            using var engine = this.Seed();
            using var finish = new ManualResetEventSlim();
            var reader = PinOnIdleThread(engine, finish, out var owner);

            RunPromptly(reader.Dispose);

            File.Exists(this.LogFilename).Should().BeFalse("ending the pin closes its engine with a full checkpoint");
            this.WriteFromAnotherInstance();
            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
        }

        [Fact]
        public void Dispose_on_another_thread_ends_the_pin_without_throwing()
        {
            var engine = this.Seed();
            using var finish = new ManualResetEventSlim();
            var reader = PinOnIdleThread(engine, finish, out var owner);

            RunPromptly(engine.Dispose);

            this.WriteFromAnotherInstance();
            RunPromptly(reader.Dispose);
            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
        }

        [Fact]
        public void Pinning_thread_that_exits_without_disposing_its_reader_releases_the_mutex()
        {
            using var engine = this.Seed();
            var reader = PinOnIdleThread(engine, finish: null, out var owner);
            owner.Join(Prompt).Should().BeTrue();

            this.WriteFromAnotherInstance();
            RunPromptly(() => engine.Update("docs", new[] { Doc(2, 1) }));
            // The snapshot's buffers must still be returned before finalization.
            RunPromptly(reader.Dispose);
        }

        [Fact]
        public void Idle_pin_expires_for_a_live_owner_that_keeps_its_reader_open()
        {
            using var engine = this.Seed();
            engine.PinIdleLimit = TimeSpan.FromMilliseconds(100);
            using var finish = new ManualResetEventSlim();
            var reader = PinOnIdleThread(engine, finish, out var owner);

            // Another instance cannot ask for the mutex; only the idle limit ends the pin.
            this.WriteFromAnotherInstance();

            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
            RunPromptly(reader.Dispose);
        }

        [Fact]
        public void Another_thread_of_the_same_instance_ends_an_idle_pin()
        {
            using var engine = this.Seed();
            using var finish = new ManualResetEventSlim();
            var reader = PinOnIdleThread(engine, finish, out var owner);

            RunPromptly(() => engine.Insert("other", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32));

            finish.Set();
            owner.Join(Prompt).Should().BeTrue();
            RunPromptly(reader.Dispose);
            engine.Query("other", new Query()).ToEnumerable().Count().Should().Be(1);
        }

        [Fact]
        public void Tight_write_loop_lets_another_thread_of_the_same_instance_in()
        {
            using var engine = this.Seed();
            using var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });

            using var inserted = new ManualResetEventSlim();
            var other = new Thread(() =>
            {
                engine.Insert("other", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
                inserted.Set();
            }) { IsBackground = true };
            other.Start();

            // The owner never pauses between writes; the holder must still let the other thread in.
            var deadline = DateTime.UtcNow + Prompt;
            var value = 2;
            while (!inserted.IsSet && DateTime.UtcNow < deadline)
                engine.Update("docs", new[] { Doc(1, value++) });

            inserted.IsSet.Should().BeTrue("a pin must not starve other threads of its instance");
            other.Join(Prompt).Should().BeTrue();
        }

        [Fact]
        public async Task Reader_disposed_after_an_await_ends_the_pin()
        {
            using var engine = this.Seed();
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });

            await Task.Run(reader.Dispose);

            File.Exists(this.LogFilename).Should().BeFalse();
            this.WriteFromAnotherInstance();
        }

        [Fact]
        public void Nested_readers_share_one_pin_until_the_last_is_disposed()
        {
            using var engine = this.Seed();
            var outer = engine.Query("docs", new Query());
            var inner = engine.Query("docs", new Query());
            outer.Read().Should().BeTrue();
            inner.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });
            var opens = engine.EngineOpens;

            engine.Update("docs", new[] { Doc(2, 1) });
            inner.Dispose();
            engine.Update("docs", new[] { Doc(3, 1) });
            engine.EngineOpens.Should().Be(opens, "the outer reader still pins the engine");

            outer.Dispose();
            File.Exists(this.LogFilename).Should().BeFalse();
            this.WriteFromAnotherInstance();
            engine.Query("docs", new Query { Where = { BsonExpression.Create("value = 1") } })
                .ToEnumerable().Count().Should().Be(3);
        }

        [Fact]
        public void Failed_write_inside_the_iteration_keeps_the_pin_balanced()
        {
            using (var engine = this.Seed())
            using (var db = new LiteDatabase(engine))
            {
                var col = db.GetCollection("docs");
                var failures = 0;
                foreach (var doc in col.FindAll().Take(20))
                {
                    doc["value"] = 1;
                    col.Update(doc);
                    Action duplicate = () => col.Insert(Doc(doc["_id"].AsInt32, 2));
                    duplicate.Should().Throw<LiteException>();
                    failures++;
                }
                failures.Should().Be(20);
                col.Count(Query.EQ("value", 1)).Should().Be(20);
            }

            File.Exists(this.LogFilename).Should().BeFalse();
            this.WriteFromAnotherInstance();
        }

        [Fact]
        public void Write_query_reader_under_a_pin_can_be_disposed_on_another_thread()
        {
            using var engine = this.Seed();
            var leased = engine.Query("docs", new Query());
            leased.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });

            var forUpdate = engine.Query("docs", new Query { ForUpdate = true });
            forUpdate.Read().Should().BeTrue();
            RunPromptly(forUpdate.Dispose);
            engine.Update("docs", new[] { Doc(2, 1) });

            RunPromptly(leased.Dispose);
            File.Exists(this.LogFilename).Should().BeFalse();
            this.WriteFromAnotherInstance();
        }

        [Fact]
        public void Pinned_transaction_rejects_completion_from_another_thread()
        {
            using var engine = this.Seed();
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });
            engine.BeginTrans().Should().BeTrue();
            engine.Update("docs", new[] { Doc(2, 1) });

            Exception foreign = null;
            RunPromptly(() =>
            {
                try { engine.Commit(); }
                catch (LiteException ex) { foreign = ex; }
            });
            foreign.Should().NotBeNull();
            foreign.Message.Should().Contain("same thread");

            engine.Commit().Should().BeTrue();
            reader.Dispose();
            this.WriteFromAnotherInstance();
            engine.Query("docs", new Query { Where = { BsonExpression.Create("value = 1") } })
                .ToEnumerable().Count().Should().Be(2);
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task Another_process_waits_only_while_the_pin_is_in_use()
        {
            using var engine = this.Open(expire: true);
            engine.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });

            // The owner keeps its reader open and stops calling in: the process
            // cannot signal this instance, so the idle limit must end the pin.
            await MvccProcess.Run("insert", this.Filename, null, "1000");

            engine.Update("docs", new[] { Doc(2, 1) });
            reader.Dispose();
            engine.Query("docs", new Query()).ToEnumerable().Count().Should().Be(Count + 20);
        }
#endif

        private void WriteFromAnotherInstance()
        {
            // Another instance shares only the named mutex, like another process.
            RunPromptly(() =>
            {
                using var other = this.Open();
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
            thread.Join(Prompt).Should().BeTrue("a pin must never keep the mutex from other threads or processes");
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
