using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
                nextFailure.Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(failure,
                    "the contextual closed-engine failure must preserve the original completion cause");
            }
            using (var reopened = new LiteDatabase(new LiteEngine(settings)))
            {
                var col = reopened.GetCollection("rows");
                col.FindById(1)["payload"].AsString.Should().Be("committed control");
                col.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(1);
            }
        }

        [Theory]
        [InlineData("commit")]
        [InlineData("rollback")]
        public void Non_io_completion_failure_tells_other_threads_to_dispose_and_reopen(string phase)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var injected = new InvalidOperationException("injected completion failure");
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var col = db.GetCollection("rows");
            col.Insert(new BsonDocument { ["_id"] = 1 });
            db.BeginTrans().Should().BeTrue();
            col.Insert(new BsonDocument { ["_id"] = 2, ["payload"] = new string('x', 20000) });
            engine.SimulateDiskWriteFail = _ => throw injected;

            var failure = Record.Exception(() => { if (phase == "commit") db.Commit(); else db.Rollback(); });
            var next = Task.Run(() => Record.Exception(() => col.Count())).GetAwaiter().GetResult();

            failure.Should().BeSameAs(injected, "the failing caller keeps the original exception");
            var closed = next.Should().BeOfType<LiteException>().Which;
            closed.ErrorCode.Should().Be(LiteException.ENGINE_DISPOSED);
            closed.Message.Should().Contain("Engine closed").And.Contain("Dispose and reopen the database before retrying")
                .And.Contain("injected completion failure");
            closed.InnerException.Should().BeSameAs(injected);
        }

        [Fact]
        public void Completion_failure_caused_by_disposing_the_engine_still_reports_a_disposed_engine()
        {
            var state = new EngineState(null, new EngineSettings()) { Disposed = true };

            state.Stop(new ObjectDisposedException("TransactionMonitor"));

            Action validate = state.Validate;
            validate.Should().Throw<LiteException>().Which.Message.Should().Be("This engine instance already disposed.");
        }

        private static IEnumerable<BsonDocument> DuplicateAfterAllocating(FailingLog log)
        {
            yield return new BsonDocument { ["_id"] = 2, ["payload"] = new string('x', 20000) };
            log.FailWrites = true;
            yield return new BsonDocument { ["_id"] = 1 };
        }
    }
}
