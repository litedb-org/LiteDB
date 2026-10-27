#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class SameThreadTransactionCrosscheck_Tests
    {
        private readonly ITestOutputHelper _output;
        public SameThreadTransactionCrosscheck_Tests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("direct")]
        [InlineData("legacy")]
        public void Foreign_facade_pragma_write_has_the_same_thread_code(string mode)
        {
            _output.WriteLine(RuntimeInformation.FrameworkDescription);
            using var file = new MappedTestFile();
            using var owner = Open(file.Filename, mode);
            using var peer = Open(file.Filename, mode);
            owner.BeginTrans().Should().BeTrue();
            try
            {
                var failure = Record.Exception(() => peer.UserVersion = 73);
                failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(141);
            }
            finally { owner.Rollback(); }
            peer.UserVersion.Should().Be(0);
            peer.UserVersion = 73;
            peer.UserVersion.Should().Be(73);
        }

        [Theory]
        [InlineData("direct")]
        [InlineData("legacy")]
        public void Implicitly_aborted_transaction_releases_same_thread_peer_write(string mode)
        {
            _output.WriteLine(RuntimeInformation.FrameworkDescription);
            using var file = new MappedTestFile();
            using var owner = Open(file.Filename, mode);
            using var peer = Open(file.Filename, mode);
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            owner.BeginTrans().Should().BeTrue();
            Exception failure;
            try
            {
                var duplicate = Record.Exception(() => owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 }));
                duplicate.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(110);
                failure = Record.Exception(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 }));
                Task.Run(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 }))
                    .Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the ended transaction returns its native hold too");
            }
            finally { owner.Rollback(); }
            if (failure != null) peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            peer.GetCollection("docs").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id).Should().Equal(1, 2, 3);
            owner.Dispose();
            peer.Dispose();
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file.Filename);
                cold.GetCollection("docs").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id).Should().Equal(1, 2, 3);
            }
            failure.Should().BeNull("the core has already rolled back the explicit transaction");
        }

        [Theory]
        [InlineData("direct")]
        [InlineData("legacy")]
        public void Pragma_refusal_preserves_owner_transaction_and_header(string mode)
        {
            using var file = new MappedTestFile();
            using (var owner = Open(file.Filename, mode))
            using (var peer = Open(file.Filename, mode))
            {
                owner.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42 });
                owner.BeginTrans().Should().BeTrue();
                try
                {
                    Record.Exception(() => owner.UserVersion = 73).Should().BeOfType<LiteException>()
                        .Which.ErrorCode.Should().Be(126, "the owner's existing pragma restriction remains");
                    Record.Exception(() => peer.UserVersion = 73).Should().BeOfType<LiteException>()
                        .Which.ErrorCode.Should().Be(LiteException.SAME_THREAD_TRANSACTION);
                    Record.Exception(() => peer.UserVersion = 0).Should().BeOfType<LiteException>()
                        .Which.ErrorCode.Should().Be(LiteException.SAME_THREAD_TRANSACTION, "a no-op write has the same admission rule");
                    owner.UserVersion.Should().Be(0);
                    owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 99 });
                }
                finally { owner.Rollback().Should().BeTrue(); }
                peer.GetCollection("docs").FindAll().Should().BeEmpty();
                peer.UserVersion = 73;
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file.Filename);
                cold.UserVersion.Should().Be(73);
                cold.GetCollection("docs").FindAll().Should().BeEmpty();
                cold.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 42 } });
            }
        }

        [OneFileFact]
        public void Mapped_foreign_pragma_has_same_thread_code() =>
            Foreign_facade_pragma_write_has_the_same_thread_code("mapped");

        [OneFileFact]
        public void Mapped_implicit_abort_releases_peer_write() =>
            Implicitly_aborted_transaction_releases_same_thread_peer_write("mapped");

        [OneFileFact]
        public void Mapped_pragma_refusal_preserves_owner_and_header() =>
            Pragma_refusal_preserves_owner_transaction_and_header("mapped");

        private static LiteDatabase Open(string filename, string mode)
            => mode == "mapped" ? new LiteDatabase(new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true }))
            : new LiteDatabase(new ConnectionString { Filename = filename, Connection = mode == "legacy" ? ConnectionType.Shared : ConnectionType.Direct });
    }
}
#endif
