#if NET8_0_OR_GREATER
using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureCoherenceReview_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Reacquired_writer_and_warm_readers_observe_remote_commits(bool checkpoint)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.Update("docs", new[] { Row(1, 0) });
                SharedOneFileWarmReads_Tests.Warm(engine, db);
                using var old = engine.Query("docs", new Query());
                old.Read().Should().BeTrue();
                var original = Core(engine);
                using (var remote = new MvccProcess("one-file-warm-writer", files.Filename, null))
                {
                    remote.Expect("ready").GetAwaiter().GetResult();
                    remote.Send("commit"); remote.Expect("committed").GetAwaiter().GetResult();
                    if (checkpoint) { remote.Send("checkpoint"); remote.Expect("checkpointed").GetAwaiter().GetResult(); }
                    for (var n = 0; n < 3; n++) db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 1));
                    engine.Update("docs", new[] { Row(2, 2) }).Should().Be(1);
                    Core(engine).Should().NotBeSameAs(original);
                    db.GetCollection("docs").Find(Query.EQ("value", 1)).Should().BeEquivalentTo(new[] { Row(1, 1) });
                    db.GetCollection("docs").Find(Query.EQ("value", 2)).Should().BeEquivalentTo(new[] { Row(2, 2) });
                    remote.Send("exit"); remote.Finish().GetAwaiter().GetResult();
                }
                var rows = new[] { old.Current.AsDocument }.Concat(ReadRest(old)).ToArray();
                rows.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            }
            using var cold = new LiteDatabase(files.Filename);
            cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(
                Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? 1 : id == 2 ? 2 : 0)));
            cold.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
        }

        private static System.Collections.Generic.IEnumerable<BsonDocument> ReadRest(IBsonDataReader reader)
        {
            while (reader.Read()) yield return reader.Current.AsDocument;
        }
    }
}
#endif
