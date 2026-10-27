#if NET8_0_OR_GREATER
using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class SameThreadMaintenance_Tests
    {
        [Theory]
        [InlineData("direct", "checkpoint")]
        [InlineData("direct", "rebuild")]
        [InlineData("legacy", "checkpoint")]
        [InlineData("legacy", "rebuild")]
        public void Foreign_maintenance_applies_code_141_and_preserves_the_owner(string mode, string operation)
        {
            using var file = new MappedTestFile();
            Exception failure;
            using (var owner = Open(file.Filename, mode))
            using (var peer = Open(file.Filename, mode))
            {
                owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 10, ["value"] = "committed" });
                owner.BeginTrans().Should().BeTrue();
                try
                {
                    owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 20, ["value"] = "aborted" });
                    failure = Record.Exception(() => { if (operation == "checkpoint") peer.Checkpoint(); else peer.Rebuild(); });
                    owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 30, ["value"] = "also aborted" });
                }
                finally { owner.Rollback().Should().BeTrue(); }
                peer.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(10);
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file.Filename);
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 10, ["value"] = "committed" } });
            }
            failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(141);
        }

        [OneFileTheory]
        [InlineData("checkpoint")]
        [InlineData("rebuild")]
        public void Mapped_foreign_maintenance_preserves_owner_and_cold_state(string operation) =>
            Foreign_maintenance_applies_code_141_and_preserves_the_owner("mapped", operation);

        [Theory]
        [InlineData("checkpoint")]
        [InlineData("rebuild")]
        public void Direct_multiple_facades_without_a_foreign_transaction_keep_maintenance_restriction(string operation)
        {
            using var file = new MappedTestFile();
            using (var owner = Open(file.Filename, "direct"))
            using (var peer = Open(file.Filename, "direct"))
            {
                owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 10, ["value"] = "committed" });
                Record.Exception(() => { if (operation == "checkpoint") peer.Checkpoint(); else peer.Rebuild(); })
                    .Should().BeOfType<InvalidOperationException>();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file.Filename);
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 10, ["value"] = "committed" } });
            }
        }

        private static LiteDatabase Open(string path, string mode) => mode == "mapped"
            ? new LiteDatabase(new SharedEngine(new EngineSettings { Filename = path, SharedOneFile = true }))
            : new LiteDatabase(new ConnectionString { Filename = path, Connection = mode == "legacy" ? ConnectionType.Shared : ConnectionType.Direct });
    }
}
#endif
