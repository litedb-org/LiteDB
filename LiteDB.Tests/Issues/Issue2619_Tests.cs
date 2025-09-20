using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2619_Tests
{
    [Fact]
    public void Cache_Should_Respect_Configured_Limit()
    {
        const int limitBytes = 1 * 1024 * 1024;
        var connectionString = $"Filename=:memory:;Cache Size={limitBytes}";

        using var db = new LiteDatabase(connectionString);
        var collection = db.GetCollection<BsonDocument>("messages");

        for (var i = 0; i < 200; i++)
        {
            var doc = new BsonDocument
            {
                ["_id"] = i,
                ["accountId"] = i % 5,
                ["folderPath"] = "inbox",
                ["payload"] = new byte[1024]
            };

            collection.Insert(doc);
        }

        for (var i = 0; i < 100; i++)
        {
            var account = i % 5;

            var results = collection
                .Find(Query.EQ("accountId", account))
                .OrderByDescending(x => x["_id"].AsInt32)
                .ToList();

            results.Should().NotBeEmpty();
        }

        var cache = GetCache(db);

        var allocatedBytes = cache.ExtendPages * Constants.PAGE_SIZE;

        allocatedBytes.Should().BeLessThanOrEqualTo(limitBytes);
    }

    private static MemoryCache GetCache(LiteDatabase database)
    {
        var engineField = typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
        var engine = (LiteEngine)engineField!.GetValue(database)!;

        var diskField = typeof(LiteEngine).GetField("_disk", BindingFlags.NonPublic | BindingFlags.Instance);
        var disk = (DiskService)diskField!.GetValue(engine)!;

        return disk.Cache;
    }
}
