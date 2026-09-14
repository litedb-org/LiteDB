using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FluentAssertions;

using LiteDB.Engine;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2169_CheckpointFailureTests
    {
        [Fact]
        public void Parallel_inserts_preserve_the_checkpoint_error_and_every_committed_payload()
        {
            using var data = new DeniedCheckpointStream();
            using var log = new MemoryStream();
            var failures = new ConcurrentQueue<Exception>();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("worker");
                Parallel.For(0, 8, worker =>
                {
                    for (var sequence = 0; sequence < 100; sequence++)
                    {
                        rows.Insert(Row(worker * 100 + sequence + 1));
                    }
                });
                db.Checkpoint();
                db.CheckpointSize = 1;
                data.Armed = true;
                Parallel.For(801, 809, id =>
                {
                    var failure = Record.Exception(() => rows.Insert(Row(id)));
                    if (failure != null) failures.Enqueue(failure);
                });
                data.Failures.Should().Be(1, "one actual checkpoint data-stream write must fail");
                data.Armed.Should().BeFalse();
                db.Checkpoint();
            }
            // A checkpoint follows commit. Even the insert whose caller received an
            // exception must exist; throwing earlier to avoid rollback is not a fix.
            // A byte-array MemoryStream has fixed capacity. Recovery writes must be
            // allowed to allocate pages regardless of the concurrent insert layout.
            var image = data.ToArray();
            using var recoveredData = new MemoryStream();
            recoveredData.Write(image, 0, image.Length);
            recoveredData.Position = 0;
            using (var reopened = new LiteDatabase(recoveredData))
            {
                VerifyRows(reopened, 808);
                reopened.GetCollection("rows").Insert(Row(809));
                reopened.GetCollection("rows").FindById(809)["payload"].AsString.Should().Be(Row(809)["payload"].AsString);
                reopened.Checkpoint();
            }
            using (var final = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(recoveredData.ToArray(), writable: false),
                ReadOnly = true
            })))
            {
                VerifyRows(final, 809);
            }
            failures.Should().ContainSingle().Which.Should().BeSameAs(data.Failure,
                "rollback of the disposed transaction must not mask the original write failure");
        }

        private static void VerifyRows(LiteDatabase db, int count)
        {
            var collection = db.GetCollection("rows");
            var rows = collection.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            rows.Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            foreach (var row in rows)
            {
                var id = row["_id"].AsInt32;
                row["worker"].AsInt32.Should().Be((id - 1) / 100);
                row["payload"].AsString.Should().Be(new string((char)('A' + id % 26), 500));
            }
            collection.Find(Query.EQ("worker", 8)).Select(row => row["_id"].AsInt32).OrderBy(id => id)
                .Should().Equal(Enumerable.Range(801, count - 800));
        }

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["worker"] = (id - 1) / 100,
            ["payload"] = new string((char)('A' + id % 26), 500)
        };

        private sealed class DeniedCheckpointStream : MemoryStream
        {
            public readonly UnauthorizedAccessException Failure = new UnauthorizedAccessException("issue 2169: checkpoint access denied");
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
