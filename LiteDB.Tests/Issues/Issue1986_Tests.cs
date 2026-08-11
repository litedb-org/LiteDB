using System.IO;
using System.Linq;
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
        public string Name { get; set; }
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
    public void Generic_min_max_on_empty_collection_uses_deserialization_hook()
    {
        var mapper = new BsonMapper
        {
            OnDeserialization = (sender, target, value) =>
                target == typeof(string) && value.IsNull ? new BsonValue("empty") : value
        };

        using var db = new LiteDatabase(new MemoryStream(), mapper);
        var col = EmptyCollection(db);

        Assert.Equal("empty", col.Max(x => x.Name));
        Assert.Equal("empty", col.Min(x => x.Name));
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

    [Fact]
    public void Typed_min_should_not_turn_a_stored_BSON_null_into_zero()
    {
        using var db = new LiteDatabase(new MemoryStream());

        db.GetCollection("e").Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Value"] = BsonValue.Null
        });

        var typed = db.GetCollection<Entity>("e");

        // Entity.Value is a non-nullable int. Returning 0 would invent a value
        // which was never stored, so this incompatible read must fail visibly.
        var exception = Record.Exception(() => typed.Min(x => x.Value));

        Assert.NotNull(exception);
    }

    [Fact]
    public void Filtered_Max_call_from_issue_1986_should_handle_no_matches()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("e");
        col.Insert(new Entity { Id = 1, Value = 10 });

        int max = -1;
        var exception = Record.Exception(() =>
            max = col.Find(x => x.Id == 999).Max(x => x.Value));

        Assert.Null(exception);
        Assert.Equal(0, max);
    }
}
