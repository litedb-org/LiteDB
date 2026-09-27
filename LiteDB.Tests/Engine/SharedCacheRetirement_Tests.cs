#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCacheRetirement_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Stale_cache_retires_before_waiting_for_writer_ownership_and_keeps_active_readers(bool streaming)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-retire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = file });
                engine.CoordinatedIdleLimit = TimeSpan.FromMinutes(1);
                using var database = new LiteDatabase(engine, disposeOnClose: false);
                using var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
                var rows = database.GetCollection("rows");
                BsonDocument Row(int id, int value) => new BsonDocument
                    { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 4000) };
                rows.InsertBulk(Enumerable.Range(0, 50).Select(id => Row(id, 7)));
                for (var i = 0; i < 3; i++) rows.FindById(i)["value"].AsInt32.Should().Be(7);
                engine.HasCachedSnapshot.Should().BeTrue();
                using var held = rows.FindAll().GetEnumerator();
                if (streaming) held.MoveNext().Should().BeTrue();
                using var retired = new ManualResetEventSlim();
                engine.CoordinationStage = stage => { if (stage == "cache-retired") retired.Set(); };
                writer.BeginTrans().Should().BeTrue();
                var next = Task.Run(() => rows.FindById(0)["value"].AsInt32);
                retired.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                engine.HasCachedSnapshot.Should().BeFalse();
                next.IsCompleted.Should().BeFalse("the writer still owns the database mutex");
                writer.GetCollection("rows").Update(Enumerable.Range(0, 50).Select(id => Row(id, 11))).Should().Be(50);
                writer.Commit().Should().BeTrue();
                next.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                next.GetAwaiter().GetResult().Should().Be(11);
                if (streaming)
                {
                    var count = 0;
                    do
                    {
                        held.Current["value"].AsInt32.Should().Be(7);
                        held.Current["payload"].AsString.Should().Be(new string('x', 4000));
                        count++;
                    } while (held.MoveNext());
                    count.Should().Be(50);
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
