using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class TransactionCompletion_Tests
    {
        [Fact]
        public async Task Later_concurrent_failures_preserve_the_first_fatal_cause()
        {
            var state = new EngineState(null, new EngineSettings());
            var first = new IOException("original failure");
            await Task.Run(() => state.Stop(first));
            var later = new Task[16];
            for (var i = 0; i < later.Length; i++)
                later[i] = Task.Run(() => state.Stop(new ObjectDisposedException("secondary")));
            await Task.WhenAll(later);
            Action validate = state.Validate;
            var stopped = validate.Should().Throw<IOException>().Which;
            stopped.Message.Should().Contain("Dispose and reopen");
            stopped.InnerException.Should().BeSameAs(first);
        }

        [Theory]
        [InlineData("commit")]
        [InlineData("rollback")]
        [InlineData("automatic rollback")]
        public void Non_io_completion_failure_closes_engine_and_releases_transactions(string phase)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            var injected = new InvalidOperationException("completion failed");
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
                Action fail = () => engine.SimulateDiskWriteFail = _ => throw injected;
                Action complete;
                if (phase == "automatic rollback")
                {
                    complete = () => rows.Insert(DuplicateAfterAllocation(fail));
                }
                else
                {
                    db.BeginTrans();
                    rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = new string('x', 20000) });
                    fail();
                    complete = () => { if (phase == "commit") db.Commit(); else db.Rollback(); };
                }
                complete.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(injected);
                engine.GetMonitor().Transactions.Should().BeEmpty();
                Action insert = () => rows.Insert(new BsonDocument { ["_id"] = 3 });
                insert.Should().Throw<LiteException>().Which.InnerException.Should().BeSameAs(injected);
            }
            using var reopened = new LiteDatabase(new LiteEngine(settings));
            reopened.GetCollection("rows").Count().Should().Be(1);
            Assert.NotNull(reopened.GetCollection("rows").FindById(1));
        }

        private static IEnumerable<BsonDocument> DuplicateAfterAllocation(Action fail)
        {
            yield return new BsonDocument { ["_id"] = 2, ["value"] = new string('x', 20000) };
            fail();
            yield return new BsonDocument { ["_id"] = 1 };
        }
    }
}
