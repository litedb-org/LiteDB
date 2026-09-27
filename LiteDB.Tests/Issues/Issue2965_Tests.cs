using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2965_Tests
    {
        [Fact]
        public async Task Rebuild_waits_for_an_active_writer_before_replacing_engine_services()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var exclusiveWaiting = new ManualResetEventSlim();
            engine.SimulateBeforeExclusiveAdmission = exclusiveWaiting.Set;

            engine.BeginTrans().Should().BeTrue();
            var rebuild = Task.Run(() => engine.Rebuild());
            exclusiveWaiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" } },
                BsonAutoId.Int32).Should().Be(1);
            engine.Commit().Should().BeTrue();

            await rebuild;
            var query = Query.All();
            query.Where.Add(Query.EQ("_id", 1));
            using var reader = engine.Query("rows", query);
            reader.Single()["value"].AsString.Should().Be("acknowledged");
        }

        [Fact]
        public async Task Rebuild_waits_for_an_active_reader()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            var reader = engine.Query("rows", Query.All());
            using var exclusiveWaiting = new ManualResetEventSlim();
            engine.SimulateBeforeExclusiveAdmission = exclusiveWaiting.Set;

            var rebuild = Task.Run(() => engine.Rebuild());
            exclusiveWaiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            rebuild.IsCompleted.Should().BeFalse();
            reader.Current.AsDocument["_id"].AsInt32.Should().Be(1);

            reader.Dispose();
            await rebuild;
            using var reopened = engine.Query("rows", Query.All());
            reopened.Single()["_id"].AsInt32.Should().Be(1);
        }

        [Fact]
        public async Task Operation_queued_behind_rebuild_fails_cleanly_and_can_be_retried()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var rebuildExclusive = new ManualResetEventSlim();
            using var releaseRebuild = new ManualResetEventSlim();
            using var operationWaiting = new ManualResetEventSlim();
            engine.SimulateAfterExclusiveAdmission = () =>
            {
                rebuildExclusive.Set();
                releaseRebuild.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            };
            engine.SimulateBeforeTransactionAdmission = operationWaiting.Set;

            var rebuild = Task.Run(() => engine.Rebuild());
            rebuildExclusive.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var insert = Task.Run(() => Record.Exception(() => engine.Insert("rows",
                new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32)));
            operationWaiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            releaseRebuild.Set();

            await rebuild;
            var failure = await insert;
            failure.Should().BeOfType<LiteException>().Which.Message.Should().Contain("disposed");

            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32)
                .Should().Be(1);
        }
    }
}
