#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureCoherenceBreadth_Tests
    {
        [OneFileTheory]
        [InlineData(null, false)] [InlineData(null, true)]
        [InlineData("secret", false)] [InlineData("secret", true)]
        public void Remote_metadata_replacement_invalidates_retained_core_and_preserves_old_snapshot(string password, bool legacy)
        {
            using var files = new SharedOneFilePublicationFixture(password, legacy);
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.Update("docs", new[] { Row(1, 0) });
                SharedOneFileWarmReads_Tests.Warm(engine, db);
                using var old = engine.Query("docs", new Query());
                old.Read().Should().BeTrue();
                var core = Core(engine);
                using var remote = new MvccProcess("one-file-warm-writer", files.Filename, password);
                remote.Expect("ready").GetAwaiter().GetResult();
                remote.Send("metadata"); remote.Expect("metadata-done").GetAwaiter().GetResult();
                db.GetCollection("docs").Find(Query.EQ("value", 3)).Should().BeEquivalentTo(Enumerable.Range(1, 3).Select(id => Row(id, 3)));
                db.GetCollection("archive").Find(Query.EQ("$.value + 10", 10)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                db.GetCollectionNames().Should().BeEquivalentTo(new[] { "docs", "archive", "sentinel" });
                engine.Update("docs", new[] { Row(1, 4) }).Should().Be(1);
                Core(engine).Should().NotBeSameAs(core);
                new[] { old.Current.AsDocument }.Concat(Rows(old)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                remote.Send("exit"); remote.Finish().GetAwaiter().GetResult();
            }
            using var cold = new LiteDatabase(new LiteDB.Engine.LiteEngine(files.Settings));
            cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 3).Select(id => Row(id, id == 1 ? 4 : 3)));
            cold.GetCollection("docs").Find(Query.EQ("value", 4)).Should().BeEquivalentTo(new[] { Row(1, 4) });
            cold.GetCollection("archive").Find(Query.EQ("$.value + 10", 10)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            cold.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
        }

        [OneFileTheory]
        [InlineData(null, false)] [InlineData(null, true)]
        [InlineData("secret", false)] [InlineData("secret", true)]
        public void Repeated_remote_full_WAL_reset_does_not_reuse_a_stale_cached_read_version(string password, bool legacy)
        {
            using var files = new SharedOneFilePublicationFixture(password, legacy);
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            using (var remote = new MvccProcess("one-file-warm-writer", files.Filename, password))
            {
                remote.Expect("ready").GetAwaiter().GetResult();
                engine.Update("docs", new[] { Row(1, 0) });
                long? repeatedVersion = null;
                for (var value = 1; value <= 4; value++)
                {
                    engine.CoordinatedIdleLimit = TimeSpan.FromMinutes(1);
                    System.Threading.SpinWait.SpinUntil(() =>
                    {
                        db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, value - 1));
                        return engine.HasCachedSnapshot;
                    }, TimeSpan.FromSeconds(5)).Should().BeTrue("a stable pre-reset cache must actually be installed");
                    var cached = engine.MappedReaderTable.ReadCache.Snapshot;
                    if (value > 2) cached.Status.Version.Should().Be(repeatedVersion.Value,
                        "full WAL reset/reuse must actually collide with the previous cached read version");
                    repeatedVersion = cached.Status.Version;
                    remote.Send("reset " + value); remote.Expect("reset-done").GetAwaiter().GetResult();
                    Verify(db, value);
                    engine.Update("docs", new[] { Row(1, value) }).Should().Be(1);
                    engine.MappedReaderTable.ReadCache.Snapshot.Should().NotBeSameAs(cached);
                }
                remote.Send("exit"); remote.Finish().GetAwaiter().GetResult();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(new LiteDB.Engine.LiteEngine(files.Settings)); Verify(cold, 4);
            }
        }

        private static IEnumerable<BsonDocument> Rows(IBsonDataReader reader)
        {
            while (reader.Read()) yield return reader.Current.AsDocument;
        }
    }
}
#endif
