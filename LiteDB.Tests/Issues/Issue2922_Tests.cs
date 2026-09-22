using System.Linq;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2922_Tests
{
    private static LiteDatabase CreateDatabase()
    {
        var db = new LiteDatabase(":memory:");

        db.GetCollection("rows").InsertBulk(Enumerable.Range(1, 20).Select(i =>
            new BsonDocument { ["_id"] = i, ["City"] = "City" + (i % 3) }));

        return db;
    }

    private static int Consume(ILiteDatabase db, string sql, BsonDocument parameters)
    {
        using var reader = db.Execute(sql, parameters);
        var rows = 0;

        while (reader.Read()) rows++;

        return rows;
    }

    [Fact]
    public void Group_by_leaves_the_callers_parameters_untouched()
    {
        using var db = CreateDatabase();
        var parameters = new BsonDocument { ["min"] = 5 };

        Consume(db, "SELECT { city: @key, n: COUNT(*) } FROM rows WHERE _id > @min GROUP BY City", parameters);

        parameters.Keys.Should().BeEquivalentTo(new[] { "min" });
        parameters["min"].AsInt32.Should().Be(5);
    }

    [Fact]
    public void Group_by_does_not_overwrite_a_caller_parameter_named_key()
    {
        using var db = CreateDatabase();
        var parameters = new BsonDocument { ["key"] = 5 };

        Consume(db, "SELECT { city: @key, n: COUNT(*) } FROM rows WHERE _id > @key GROUP BY City", parameters);

        parameters["key"].AsInt32.Should().Be(5);
        Consume(db, "SELECT _id FROM rows WHERE _id > @key", parameters).Should().Be(15);
    }

    [Fact]
    public void Having_leaves_the_callers_parameters_untouched()
    {
        using var db = CreateDatabase();
        var parameters = new BsonDocument { ["least"] = 6 };

        var rows = Consume(db, "SELECT { city: @key, n: COUNT(*) } FROM rows GROUP BY City HAVING COUNT(*) >= @least", parameters);

        rows.Should().Be(3);
        parameters.Keys.Should().BeEquivalentTo(new[] { "least" });
        parameters["least"].AsInt32.Should().Be(6);
    }

    [Fact]
    public void Group_by_with_order_by_leaves_the_callers_parameters_untouched()
    {
        using var db = CreateDatabase();
        var parameters = new BsonDocument { ["min"] = 5 };

        Consume(db, "SELECT { city: @key, n: COUNT(*) } FROM rows WHERE _id > @min GROUP BY City ORDER BY @key", parameters);

        parameters.Keys.Should().BeEquivalentTo(new[] { "min" });
    }

    [Fact]
    public void Group_by_still_binds_the_key_parameter_in_the_projection()
    {
        using var db = CreateDatabase();
        var parameters = new BsonDocument();

        using var reader = db.Execute("SELECT { city: @key, n: COUNT(*) } FROM rows GROUP BY City ORDER BY @key", parameters);
        var cities = new System.Collections.Generic.List<string>();

        while (reader.Read()) cities.Add(reader.Current["city"].AsString);

        cities.Should().Equal("City0", "City1", "City2");
    }

    [Fact]
    public void Group_by_does_not_disturb_an_indexed_filter_while_it_is_still_being_read()
    {
        using var db = CreateDatabase();
        db.GetCollection("rows").EnsureIndex("City");

        var parameters = new BsonDocument { ["key"] = "City0" };
        var cities = new System.Collections.Generic.List<string>();

        using (var reader = db.Execute("SELECT { city: @key, n: COUNT(*) } FROM rows WHERE City != @key GROUP BY City", parameters))
        {
            while (reader.Read()) cities.Add(reader.Current["city"].AsString);
        }

        cities.Should().BeEquivalentTo(new[] { "City1", "City2" });
        parameters["key"].AsString.Should().Be("City0");
    }

    [Fact]
    public void Group_by_isolates_expressions_that_share_a_parameter_name()
    {
        using var db = CreateDatabase();

        var select = BsonExpression.Create("{ city: @key, n: COUNT(*) }");
        var having = BsonExpression.Create("COUNT(*) >= @least");

        select.Parameters["key"] = "unused";
        having.Parameters["least"] = 6;

        var parameters = new BsonDocument { ["key"] = "caller", ["least"] = 6 };
        var cities = new System.Collections.Generic.List<string>();

        using (var reader = db.Execute("SELECT { city: @key, n: COUNT(*) } FROM rows GROUP BY City HAVING COUNT(*) >= @least", parameters))
        {
            while (reader.Read()) cities.Add(reader.Current["city"].AsString);
        }

        cities.Should().BeEquivalentTo(new[] { "City0", "City1", "City2" });
        parameters["key"].AsString.Should().Be("caller");
        parameters["least"].AsInt32.Should().Be(6);
        select.Parameters["key"].AsString.Should().Be("unused");
        having.Parameters["least"].AsInt32.Should().Be(6);
    }
}
