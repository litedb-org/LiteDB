#if NET8_0_OR_GREATER
using System;
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
    public class SharedCachedIdleTimer_Tests
    {
        [MappedFact]
        public void Timer_visit_during_streaming_keeps_the_snapshot_and_cleanup_remains_bounded()
        {
            using var file = new MappedTestFile();
            using (var seed = new LiteDatabase(file.Filename))
                seed.GetCollection("rows").InsertBulk(Enumerable.Range(0, 200).Select(id =>
                    new BsonDocument { ["_id"] = id, ["value"] = 7, ["payload"] = new string('p', 4000) }));
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
            using var database = new LiteDatabase(engine);
            using var registry = new SharedReaderRegistry(file.Filename);
            var rows = database.GetCollection("rows");
            for (var i = 0; i < 3; i++) rows.FindById(i)["value"].AsInt32.Should().Be(7);
            engine.CoordinatedReadHits.Should().BeGreaterThan(0);
            using (var stream = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator())
            {
                stream.MoveNext().Should().BeTrue();
                registry.LiveVersions().Should().NotBeEmpty();
                engine.CoordinatedIdleLimit = TimeSpan.FromMilliseconds(25);
                // Force the timer's visit while a real streaming reader owns the lease.
                typeof(SharedEngine).GetMethod("ExpireSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(engine, null);
                engine.HasCachedSnapshot.Should().BeTrue();
                var count = 0;
                do
                {
                    stream.Current["_id"].AsInt32.Should().Be(count++);
                    stream.Current["value"].AsInt32.Should().Be(7);
                    stream.Current["payload"].AsString.Should().Be(new string('p', 4000));
                }
                while (stream.MoveNext());
                count.Should().Be(200);
            }
            registry.LiveVersions().Should().BeEmpty();
            SpinWait.SpinUntil(() => !engine.HasCachedSnapshot, TimeSpan.FromSeconds(5)).Should().BeTrue();
            rows.FindById(199)["value"].AsInt32.Should().Be(7);
            GC.KeepAlive(database);
        }
    }
}
#endif
