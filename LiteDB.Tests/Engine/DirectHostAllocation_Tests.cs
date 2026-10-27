using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class DirectHostAllocation_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Peer_header_commit_preserves_foreign_allocations_through_owner_completion_and_reopen(
            string password, bool commit)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password };
            var pendingPayload = new string('a', 24000);
            var committedPayload = new string('b', 24000);
            using (var first = new LiteDatabase(connection))
            {
                first.CheckpointSize = 0;
                first.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 99, ["value"] = "keep" });
                first.GetCollection("pending").EnsureIndex("value");
                using var second = new LiteDatabase(connection);
                first.BeginTrans().Should().BeTrue();
                first.GetCollection("pending").Insert(Enumerable.Range(1, 8).Select(id =>
                    new BsonDocument { ["_id"] = id, ["value"] = id, ["payload"] = pendingPayload }));

                // A different thread and facade must commit its own header, retaining
                // the allocations of the still-uncommitted transaction in the core.
                Exception peerError = null;
                var peer = new Thread(() =>
                {
                    try
                    {
                        second.BeginTrans().Should().BeTrue();
                        second.GetCollection("committed").Insert(Enumerable.Range(1, 8).Select(id =>
                            new BsonDocument { ["_id"] = id, ["value"] = id, ["payload"] = committedPayload }));
                        second.GetCollection("committed").EnsureIndex("value");
                        second.Commit().Should().BeTrue();
                    }
                    catch (Exception error) { peerError = error; }
                }) { IsBackground = true };
                peer.Start();
                peer.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                peerError.Should().BeNull();

                if (commit) first.Commit().Should().BeTrue();
                else first.Rollback().Should().BeTrue();
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var reopened = new LiteDatabase(connection);
                var committed = reopened.GetCollection("committed");
                committed.Count().Should().Be(8);
                foreach (var id in Enumerable.Range(1, 8))
                {
                    committed.FindById(id)["payload"].AsString.Should().Be(committedPayload);
                    committed.FindOne(Query.EQ("value", id))["_id"].AsInt32.Should().Be(id);
                }
                var pending = reopened.GetCollection("pending");
                pending.Count().Should().Be(commit ? 8 : 0);
                foreach (var id in Enumerable.Range(1, 8))
                {
                    if (commit)
                    {
                        pending.FindById(id)["payload"].AsString.Should().Be(pendingPayload);
                        pending.FindOne(Query.EQ("value", id))["_id"].AsInt32.Should().Be(id);
                    }
                    else
                    {
                        Assert.Null(pending.FindById(id));
                        Assert.Null(pending.FindOne(Query.EQ("value", id)));
                    }
                }
                reopened.GetCollection("untouched").FindById(99)["value"].AsString.Should().Be("keep");
                // Subsequent allocation must not overwrite either surviving collection.
                reopened.GetCollection("later").Insert(new BsonDocument { ["_id"] = attempt, ["payload"] = new string('c', 48000) });
            }
        }
    }
}
