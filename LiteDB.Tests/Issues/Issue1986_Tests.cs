using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// #1986 - col.Min/Max (and the generic Min&lt;K&gt;/Max&lt;K&gt; wrappers) throw
/// "Sequence contains no elements" (InvalidOperationException) on an empty
/// collection because the aggregate pipeline ends in .First(). They should
/// return BsonValue.Null / default(K) instead.
/// </summary>
public class Issue1986_Tests
{
    public class Entity
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    private static ILiteCollection<Entity> EmptyCollection(LiteDatabase db)
        => db.GetCollection<Entity>("e");

    [Fact]
    public void Max_expression_on_empty_collection_returns_null_not_throws()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = EmptyCollection(db);

        Assert.Null(Record.Exception(() => col.Max("Value")));
        Assert.True(col.Max("Value").IsNull);
    }

    [Fact]
    public void Min_expression_on_empty_collection_returns_null_not_throws()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = EmptyCollection(db);

        Assert.Null(Record.Exception(() => col.Min("Value")));
        Assert.True(col.Min("Value").IsNull);
    }

    [Fact]
    public void Max_generic_on_empty_collection_returns_default_not_throws()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = EmptyCollection(db);

        int max = -1;
        Assert.Null(Record.Exception(() => max = col.Max(x => x.Value)));
        Assert.Equal(0, max);
    }

    [Fact]
    public void Min_generic_on_empty_collection_returns_default_not_throws()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = EmptyCollection(db);

        int min = -1;
        Assert.Null(Record.Exception(() => min = col.Min(x => x.Value)));
        Assert.Equal(0, min);
    }

    [Fact]
    public void Max_Min_return_correct_values_when_not_empty()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = EmptyCollection(db);

        col.Insert(new Entity { Id = 1, Value = 5 });
        col.Insert(new Entity { Id = 2, Value = 9 });
        col.Insert(new Entity { Id = 3, Value = 2 });

        Assert.Equal(9, col.Max(x => x.Value));
        Assert.Equal(2, col.Min(x => x.Value));
    }
}
