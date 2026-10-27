using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class LegacySharedImplicitAbort_Tests
    {
        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public void Failed_input_returns_transaction_ownership_before_any_outer_completion(bool pinned, bool stop)
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file.Filename }))
            using (var owner = new LiteDatabase(engine, disposeOnClose: false))
            using (var peer = Open(file.Filename))
            {
                using var anchor = pinned ? engine.Query("docs", new Query()) : null;
                if (pinned)
                {
                    anchor.Read().Should().BeTrue();
                    engine.Update("docs", new[] { Row(1) }).Should().Be(1);
                }
                owner.BeginTrans().Should().BeTrue();
                IEnumerable<BsonDocument> Input()
                {
                    yield return Row(99);
                    if (stop) throw new IOException("input stopped the core");
                    throw new InvalidOperationException("input aborted the transaction");
                }
                var error = Record.Exception(() => owner.GetCollection("docs").Insert(Input()));
                if (stop) error.Should().BeOfType<IOException>();
                else error.Should().BeOfType<InvalidOperationException>();
                // A reader has its own hold; end it before demanding native admission.
                anchor?.Dispose();
                AssertPeerProgress(peer);
                owner.Rollback().Should().BeFalse("the failed operation already ended the transaction");
                owner.Rollback().Should().BeFalse("terminal cleanup is repeatable");
            }
            Verify(file.Filename);
        }

        [Theory]
        [InlineData(1, false)] [InlineData(2, false)]
        [InlineData(1, true)] [InlineData(2, true)]
        public void Stopped_query_releases_transaction_at_initial_or_streamed_read(int failingId, bool pinned)
        {
            using var file = new TempFile();
            Seed(file.Filename);
            var fail = false;
            var hits = 0;
            using (var engine = new SharedEngine(new EngineSettings
            {
                Filename = file.Filename,
                ReadTransform = (_, row) =>
                {
                    if (fail && row["_id"].AsInt32 == failingId)
                    {
                        hits++;
                        throw new IOException("read stopped the core");
                    }
                    return row;
                }
            }))
            using (var owner = new LiteDatabase(engine, disposeOnClose: false))
            using (var peer = Open(file.Filename))
            {
                using var anchor = pinned ? engine.Query("docs", new Query()) : null;
                if (pinned)
                {
                    anchor.Read().Should().BeTrue();
                    engine.Update("docs", new[] { Row(1) }).Should().Be(1);
                }
                owner.BeginTrans().Should().BeTrue();
                owner.GetCollection("docs").Insert(Row(99));
                fail = true;
                Action query = () =>
                {
                    using var reader = engine.Query("docs", new Query());
                    while (reader.Read()) { }
                };
                query.Should().Throw<IOException>().WithMessage("read stopped the core");
                hits.Should().Be(1, "the real read failure must reach initial or retained execution");
                fail = false;
                anchor?.Dispose();
                AssertPeerProgress(peer);
                owner.Rollback().Should().BeFalse();
            }
            Verify(file.Filename);
        }

        private static void AssertPeerProgress(LiteDatabase peer)
        {
            peer.GetCollection("docs").Insert(Row(4));
            Task.Run(() => peer.GetCollection("docs").Insert(Row(5)))
                .Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("no transaction recursion remains");
        }

        private static LiteDatabase Open(string path) => new LiteDatabase(new ConnectionString
        { Filename = path, Connection = ConnectionType.Shared });

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };

        private static void Seed(string path)
        {
            using var db = new LiteDatabase(path);
            db.GetCollection("docs").Insert(Enumerable.Range(1, 3).Select(Row));
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(Row(42));
        }

        private static void Verify(string path)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(path);
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 5).Select(Row));
                cold.GetCollection("docs").Find(Query.EQ("value", 99)).Should().BeEmpty();
                foreach (var id in Enumerable.Range(1, 5))
                    cold.GetCollection("docs").Find(Query.EQ("value", id)).Should().BeEquivalentTo(new[] { Row(id) });
                cold.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42) });
            }
        }
    }
}
