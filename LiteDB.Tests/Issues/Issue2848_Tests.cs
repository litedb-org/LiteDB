using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2848_Tests
    {
        private sealed class FailingLog : MemoryStream
        {
            public bool FailWrites;
            public int FailureCount;
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (FailWrites) { FailureCount++; throw new IOException("injected transaction completion failure"); }
                base.Write(buffer, offset, count);
            }
        }

        [Theory]
        [InlineData("rollback")]
        [InlineData("commit")]
        [InlineData("automatic rollback")]
        public void Failed_transaction_completion_cannot_swallow_later_acknowledged_writes(string phase)
        {
            using var data = new MemoryStream();
            using var log = new FailingLog();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            bool acknowledged;
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                var col = db.GetCollection("rows");
                col.Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "committed control" });
                db.Checkpoint();
                Exception failure;
                if (phase == "automatic rollback")
                {
                    failure = Record.Exception(() => col.Insert(DuplicateAfterAllocating(log)));
                }
                else
                {
                    db.BeginTrans().Should().BeTrue();
                    col.Insert(new BsonDocument { ["_id"] = 2, ["payload"] = new string('x', 20000) });
                    log.FailWrites = true;
                    failure = Record.Exception(() => { if (phase == "commit") db.Commit(); else db.Rollback(); });
                }
                log.FailWrites = false;
                failure.Should().BeOfType<IOException>().Which.Message.Should().Be("injected transaction completion failure");
                log.FailureCount.Should().BeGreaterThan(0);
                var nextFailure = Record.Exception(() => col.Insert(new BsonDocument { ["_id"] = 50000, ["payload"] = "acknowledged after failure" }));
                acknowledged = nextFailure == null;
                if (!acknowledged)
                    (nextFailure is IOException || nextFailure is LiteException || nextFailure is ObjectDisposedException)
                        .Should().BeTrue("a closed engine must fail explicitly");
            }
            using (var reopened = new LiteDatabase(new LiteEngine(settings)))
            {
                var col = reopened.GetCollection("rows");
                col.FindById(1)["payload"].AsString.Should().Be("committed control");
                col.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(acknowledged ? new[] { 1, 50000 } : new[] { 1 });
                if (acknowledged) col.FindById(50000)["payload"].AsString.Should().Be("acknowledged after failure");
            }
        }

        private static IEnumerable<BsonDocument> DuplicateAfterAllocating(FailingLog log)
        {
            yield return new BsonDocument { ["_id"] = 2, ["payload"] = new string('x', 20000) };
            log.FailWrites = true;
            yield return new BsonDocument { ["_id"] = 1 };
        }
    }
}
