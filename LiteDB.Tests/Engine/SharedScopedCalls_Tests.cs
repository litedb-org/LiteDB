using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedScopedCalls_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-scoped-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedScopedCalls_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void A_reader_opened_by_a_lazy_write_can_outlive_the_call_and_close_off_thread()
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename });
            engine.Insert("docs", Documents(0), BsonAutoId.Int32);
            engine.MutexOwner.HasHolderThread.Should().BeFalse("array writes are scoped");
            IBsonDataReader escaped = null;
            IEnumerable<BsonDocument> Input()
            {
                yield return new BsonDocument { ["_id"] = 1 };
                escaped = engine.Query("docs", new Query { ForUpdate = true });
                yield return new BsonDocument { ["_id"] = 2 };
            }
            engine.Insert("other", Input(), BsonAutoId.Int32).Should().Be(2);
            try { OnThread(escaped.Dispose); }
            finally { escaped.Dispose(); }
            engine.Dispose();
            using var reopened = new LiteDatabase(Filename);
            reopened.GetCollection("other").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
        }

        [Fact]
        public void Failed_lease_after_a_successful_probe_retries_before_execution_and_preserves_off_thread_disposal()
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename });
            engine.Insert("docs", Documents(0), BsonAutoId.Int32);
            using (var warmup = engine.Query("docs", new Query { Limit = 1 })) warmup.Read().Should().BeTrue();
            var registry = (SharedReaderRegistry)typeof(SharedEngine).GetField("_readers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            var slots = (SharedReaderSlots)typeof(SharedReaderRegistry).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(registry);
            var direct = false;
            slots.WriteOverride = (file, bytes) =>
            {
                direct |= engine.MutexOwner.OwnsDirectly;
                throw new IOException("registration unavailable");
            };
            using var reader = engine.Query("docs", new Query());
            direct.Should().BeTrue("the tested query entered the scoped path");
            var values = new List<int>();
            while (reader.Read()) values.Add(reader.Current["_id"].AsInt32);
            values.Should().Equal(Enumerable.Range(1, 150));
            OnThread(reader.Dispose);
            slots.WriteOverride = null;
            engine.Update("docs", Documents(1)).Should().Be(150);
        }

        [Fact]
        public void A_read_transform_may_open_a_reader_that_is_disposed_off_thread()
        {
            IBsonDataReader escaped = null;
            SharedEngine engine = null;
            var entered = false;
            using (engine = new SharedEngine(new EngineSettings
            {
                Filename = Filename,
                ReadTransform = (_, value) =>
                {
                    if (!entered)
                    {
                        entered = true;
                        escaped = engine.Query("docs", new Query { ForUpdate = true, Limit = 1 });
                    }
                    return value;
                }
            }))
            {
                engine.Insert("docs", Documents(0), BsonAutoId.Int32);
                using var reader = engine.Query("docs", new Query());
                reader.Read().Should().BeTrue();
                entered.Should().BeTrue();
                OnThread(escaped.Dispose);
            }
        }

        private static BsonDocument[] Documents(int value) => Enumerable.Range(1, 150).Select(id =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 500) }).ToArray();

        private static void OnThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            error.Should().BeNull();
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
