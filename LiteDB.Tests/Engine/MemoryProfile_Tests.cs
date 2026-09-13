using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class MemoryProfile_Tests
    {
        [Theory]
        [InlineData(MemoryProfile.Balanced, 64, 8, 1000)]
        [InlineData(MemoryProfile.LowMemory, 8, 4, 256)]
        [InlineData(MemoryProfile.Throughput, 128, 16, 4000)]
        public void Defaults_AreConsistentAcrossConnectionAndStorageTypes(MemoryProfile profile, int fileMiB, int memoryMiB, int pages)
        {
            var connection = new ConnectionString("filename=:memory:;memory profile=" + profile);
            connection.TransactionPageLimit.Should().Be(pages);
            var settings = new EngineSettings { Filename = "data.db", MemoryProfile = profile };
            settings.TransactionPageLimit.Should().Be(pages);
            settings.GetCacheSize().Should().Be(fileMiB * 1024L * 1024);
            using var stream = new MemoryStream();
            settings.DataStream = stream;
            settings.GetCacheSize().Should().Be(memoryMiB * 1024L * 1024);
            using var database = new LiteDatabase(connection);
            var info = database.Execute("SELECT $ FROM $database").Single();
            info["cache"]["memoryProfile"].AsString.Should().Be(profile.ToString());
            info["cache"]["limitBytes"].AsInt64.Should().Be(memoryMiB * 1024L * 1024);
            info["transactions"]["transactionPageLimit"].AsInt32.Should().Be(pages);
        }

        [Theory]
        [InlineData("memory profile=LowMemory;cache size=32MB;transaction pages=123")]
        [InlineData("transaction pages=123;cache size=32MB;memory profile=lowmemory")]
        public void ExplicitLimits_OverrideProfileRegardlessOfTextOrder(string options)
        {
            using var database = new LiteDatabase("filename=:memory:;" + options);
            var info = database.Execute("SELECT $ FROM $database").Single();
            info["cache"]["limitBytes"].AsInt64.Should().Be(32L * 1024 * 1024);
            info["transactions"]["transactionPageLimit"].AsInt32.Should().Be(123);
        }

        [Fact]
        public void PropertyAssignment_PreservesOverrides_AndUnspecifiedLimitsFollowProfile()
        {
            var settings = new EngineSettings { CacheSize = 1234567, TransactionPageLimit = 123, MemoryProfile = MemoryProfile.LowMemory };
            settings.GetCacheSize().Should().Be(1234567);
            settings.TransactionPageLimit.Should().Be(123);
            var connection = new ConnectionString("filename=:memory:") { MemoryProfile = MemoryProfile.Throughput };
            connection.TransactionPageLimit.Should().Be(4000);
            connection.TransactionPageLimit = 123;
            connection.MemoryProfile = MemoryProfile.LowMemory;
            connection.TransactionPageLimit.Should().Be(123);
            connection.CacheSize = 0;
            using var database = new LiteDatabase(connection);
            database.Execute("SELECT $ FROM $database").Single()["cache"]["limitBytes"].AsInt64.Should().Be(4L * 1024 * 1024);
        }

        [Theory]
        [InlineData("")]
        [InlineData("auto")]
        [InlineData("1")]
        [InlineData("LowMemory,Throughput")]
        public void InvalidProfile_IsRejected(string value)
        {
            Action parse = () => new ConnectionString("filename=:memory:;memory profile=" + value);
            parse.Should().Throw<LiteException>();
        }

        [Fact]
        public void InvalidEnum_IsRejectedEvenWithExplicitLimits()
        {
            Action open = () => new LiteEngine(new EngineSettings
            {
                Filename = ":memory:", MemoryProfile = (MemoryProfile)99, CacheSize = 1024 * 1024, TransactionPageLimit = 100
            });
            open.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData(MemoryProfile.LowMemory)]
        [InlineData(MemoryProfile.Balanced)]
        [InlineData(MemoryProfile.Throughput)]
        public void BulkWriteAndIndex_RemainCorrectWithinProfileBudget(MemoryProfile profile)
        {
            using var database = new LiteDatabase(new ConnectionString(":memory:") { MemoryProfile = profile });
            var collection = database.GetCollection("docs");
            collection.InsertBulk(Enumerable.Range(0, 6000).Select(i => new BsonDocument
            {
                ["_id"] = i, ["order"] = 6000 - i, ["payload"] = new string('x', 900)
            }));
            collection.EnsureIndex("order");
            collection.Query().OrderBy("order").ToDocuments().Count().Should().Be(6000);
            database.Checkpoint();
            var cache = database.Execute("SELECT $ FROM $database").Single()["cache"];
            cache["pinnedPages"].AsInt32.Should().Be(0);
            cache["lostFrames"].AsInt64.Should().Be(0);
            cache["totalPages"].AsInt32.Should().BeLessThanOrEqualTo(cache["limitPagesRounded"].AsInt32);
        }
    }
}
