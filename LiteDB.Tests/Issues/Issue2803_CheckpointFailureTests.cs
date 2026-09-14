using System;
using System.IO;
using System.Linq;

using FluentAssertions;

using LiteDB.Engine;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2803_CheckpointFailureTests
    {
        [Fact]
        public void EnsureIndex_preserves_a_post_commit_checkpoint_error_and_the_committed_index()
        {
            using var data = new DeniedCheckpointStream();
            using var log = new MemoryStream();
            Exception failure;
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 128).Select(id => new BsonDocument
                {
                    ["_id"] = id, ["key"] = 129 - id, ["payload"] = "row-" + id
                }));
                db.Checkpoint();
                db.CheckpointSize = 1;
                data.Armed = true;
                failure = Record.Exception(() => rows.EnsureIndex("key", true));
                data.Failures.Should().Be(1, "the real post-commit data-stream write must fail");
                data.Armed.Should().BeFalse();
                // Check recovery before the diagnostic assertion, even on affected builds.
                rows.EnsureIndex("key", true).Should().BeFalse("the index transaction already committed");
                rows.Insert(new BsonDocument { ["_id"] = 129, ["key"] = 129, ["payload"] = "after-failure" });
                db.Checkpoint();
            }
            using (var reopened = new LiteDatabase(new MemoryStream(data.ToArray())))
            {
                var rows = reopened.GetCollection("rows");
                rows.FindAll().OrderBy(row => row["_id"].AsInt32).Select(row => row["payload"].AsString)
                    .Should().Equal(Enumerable.Range(1, 128).Select(id => "row-" + id).Concat(new[] { "after-failure" }));
                rows.Find(Query.EQ("key", 1)).Single()["_id"].AsInt32.Should().Be(128);
                var duplicate = Record.Exception(() => rows.Insert(new BsonDocument { ["_id"] = 130, ["key"] = 1 }));
                duplicate.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            }
            failure.Should().BeSameAs(data.Failure, "rollback of a disposed transaction must not replace the storage error");
        }

        private sealed class DeniedCheckpointStream : MemoryStream
        {
            public readonly UnauthorizedAccessException Failure = new UnauthorizedAccessException("issue 2803: checkpoint access denied");
            public bool Armed;
            public int Failures;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed)
                {
                    Armed = false;
                    Failures++;
                    throw Failure;
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
