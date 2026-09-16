using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2785_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Foreign_WAL_cannot_replace_existing_or_new_database_contents(bool existing)
        {
            using var donorData = new MemoryStream();
            using var donorLog = new MemoryStream();
            byte[] foreignLog;
            using (var donor = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = donorData, LogStream = donorLog })))
            {
                donor.CheckpointSize = 0;
                donor.GetCollection("foreign").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "belongs to donor" });
                foreignLog = donorLog.ToArray();
            }
            using var victimData = new MemoryStream();
            if (existing)
            {
                using var original = new LiteDatabase(victimData);
                original.GetCollection("own").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "belongs to victim" });
                original.Checkpoint();
            }
            var before = victimData.ToArray();
            using var injectedLog = new MemoryStream();
            injectedLog.Write(foreignLog, 0, foreignLog.Length);
            var error = Record.Exception(() =>
            {
                using var victim = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = victimData, LogStream = injectedLog }));
                victim.GetCollectionNames().Should().BeEquivalentTo(existing ? new[] { "own" } : Array.Empty<string>());
                if (existing) victim.GetCollection("own").FindById(7)["value"].AsString.Should().Be("belongs to victim");
                victim.GetCollection("foreign").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "new local row" });
                victim.GetCollection("foreign").FindById(1)["value"].AsString.Should().Be("new local row");
            });
            // Explicit rejection is also safe, provided it does not damage the original data.
            if (error is LiteException)
            {
                victimData.ToArray().Should().Equal(before);
                injectedLog.ToArray().Should().Equal(foreignLog);
            }
            else error.Should().BeNull();
        }
    }
}
