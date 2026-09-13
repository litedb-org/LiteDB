using FluentAssertions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xunit;

using static LiteDB.Tests.Issues.Issue1838_Tests;

namespace LiteDB.Tests.Issues;

public class Pull2468_Tests
{
    // tests if lowerinvariant works
    [Fact]
    public void Supports_LowerInvariant()
    {
        using var db = new LiteDatabase(":memory:");
        var collection = db.GetCollection<TestType>(nameof(TestType));

        collection.Insert(new TestType()
        {
            Foo = "Abc",
            Timestamp = DateTimeOffset.UtcNow,
        });

        collection.Insert(new TestType()
        {
            Foo = "Def",
            Timestamp = DateTimeOffset.UtcNow,
        });

        var result = collection.Query()
            .Where(x => x.Foo.ToLowerInvariant() == "abc")
            .ToList();

        Assert.NotNull(result);
        Assert.Single(result);
    }

    // tests if upperinvariant works
    [Fact]
    public void Supports_UpperInvariant()
    {
        using var db = new LiteDatabase(":memory:");
        var collection = db.GetCollection<TestType>(nameof(TestType));

        collection.Insert(new TestType()
        {
            Foo = "Abc",
            Timestamp = DateTimeOffset.UtcNow,
        });

        collection.Insert(new TestType()
        {
            Foo = "Def",
            Timestamp = DateTimeOffset.UtcNow,
        });

        var result = collection.Query()
            .Where(x => x.Foo.ToUpperInvariant() == "ABC")
            .ToList();

        Assert.NotNull(result);
        Assert.Single(result);
    }
}