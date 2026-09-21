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
    }
}
