using FluentAssertions;

using System.Diagnostics;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2487_tests
{
    private class DataClass
    {
        [BsonId]
        public int Id { get; set; }

        public string Foo { get; set; }

        public string Bar { get; set; }
    }

    [Fact]
    public void Test_Contains_EmptyStrings()
    {
        using var engine = new ConnectionString(":memory:").CreateEngine();

        using var db = new LiteDatabase(engine);
        var collection = db.GetCollection<DataClass>("data");

        collection.Insert(new DataClass { Foo = "bar", Bar = "abc" });
        collection.Insert(new DataClass { Foo = " ", Bar = "def" });
        collection.Insert(new DataClass { Foo = "fo bar", Bar = "def" });
        collection.Insert(new DataClass { Foo = "", Bar = "def" });
        collection.Insert(new DataClass { Foo = null, Bar = "def" });

        var containsAction = () => collection.FindOne(x => x.Foo.Contains(" "));
        containsAction.Should().NotThrow();

        var def = containsAction();
        def.Should().NotBeNull();
        def.Bar.Should().Be("def");

        var shouldExecute = () => engine.Query("data", Query.All(Query.Contains("Foo", " ")));
        shouldExecute.Should().NotThrow();
    }
}