using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// Wrapped collections must survive every engine path: insert, update, upsert,
/// indexes, queries, SQL, transactions, checkpoint, rebuild and file reopen.
/// </summary>
public class Issue2456_Database_Tests
{
    private static readonly string[] Tags = { "a", "b", "c" };

    private static BsonDocument NewDocument(int id, params string[] tags)
    {
        return new BsonDocument
        {
            ["_id"] = id,
            ["tags"] = new BsonValue(tags),
            ["meta"] = new BsonValue(new Dictionary<string, object> { ["count"] = tags.Length, ["first"] = tags.FirstOrDefault() })
        };
    }

    [Fact]
    public void Insert_and_read_back_should_return_native_collections_with_equal_content()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        var document = NewDocument(1, Tags);

        col.Insert(document);
        var loaded = col.FindById(1);

        loaded["tags"].Should().BeOfType<BsonArray>();
        loaded["meta"].Should().BeOfType<BsonDocument>();
        loaded["tags"].Should().Be(document["tags"]);
        loaded["meta"].Should().Be(document["meta"]);
        ((BsonValue)loaded).Should().Be(document);
    }

    [Fact]
    public void Wrapped_document_without_id_should_receive_an_auto_id_visible_through_the_wrapper()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        var wrapper = new BsonValue(new Dictionary<string, object> { ["name"] = "x" });

        var id = col.Insert(wrapper.AsDocument);

        id.IsObjectId.Should().BeTrue();
        wrapper["_id"].Should().Be(id);
        col.FindById(id)["name"].AsString.Should().Be("x");
    }

    [Fact]
    public void Insert_many_wrapped_documents_should_persist_all_of_them()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        var documents = Enumerable.Range(1, 500)
            .Select(i => new BsonValue(new Dictionary<string, object> { ["_id"] = i, ["tags"] = new[] { "t" + i } }).AsDocument)
            .ToArray();

        col.InsertBulk(documents).Should().Be(500);

        col.Count().Should().Be(500);
        col.FindById(250)["tags"].AsArray[0].AsString.Should().Be("t250");
    }

    [Fact]
    public void Update_and_upsert_with_wrapped_values_should_replace_stored_collections()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        col.Insert(NewDocument(1, "old"));

        col.Update(NewDocument(1, "new", "er")).Should().BeTrue();
        col.FindById(1)["tags"].AsArray.Select(x => x.AsString).Should().Equal("new", "er");

        col.Upsert(NewDocument(1, "up")).Should().BeFalse();
        col.Upsert(NewDocument(2, "serted")).Should().BeTrue();
        col.FindById(1)["tags"].AsArray.Select(x => x.AsString).Should().Equal("up");
        col.FindById(2)["meta"]["first"].AsString.Should().Be("serted");

        var updated = col.UpdateMany(BsonExpression.Create("{ tags: @0 }", new BsonValue(Tags)), "_id = 2");

        updated.Should().Be(1);
        col.FindById(2)["tags"].AsArray.Select(x => x.AsString).Should().Equal(Tags);
    }

    [Fact]
    public void Queries_should_accept_wrapped_values_as_parameters()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        col.Insert(NewDocument(1, "a", "b"));
        col.Insert(NewDocument(2, "c"));

        col.Count(Query.EQ("tags", new BsonValue(new[] { "a", "b" }))).Should().Be(1);
        col.Count(Query.EQ("meta", new BsonValue(new Dictionary<string, object> { ["count"] = 1L, ["first"] = "c" }))).Should().Be(1);
        col.Count(Query.In("_id", new BsonValue(new[] { 1, 2 }).AsArray)).Should().Be(2);
        col.Count(Query.In("_id", new BsonValue(new[] { 2 }).AsArray)).Should().Be(1);
        col.Count(BsonExpression.Create("$.tags = @0", new BsonValue(new[] { "c" }))).Should().Be(1);
        col.Count(BsonExpression.Create("@0 ANY = $._id", new BsonValue(new[] { 2, 3 }))).Should().Be(1);
        col.Count(BsonExpression.Create("$.tags ANY = @0", "b")).Should().Be(1);
        col.Count(BsonExpression.Create("COUNT($.tags[*]) = @0", 2)).Should().Be(1);
    }

    [Fact]
    public void Indexes_should_work_on_wrapped_array_and_document_fields()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        col.EnsureIndex("tags");
        col.EnsureIndex("items", "$.tags[*]");
        col.EnsureIndex("first", "$.meta.first");
        col.Insert(NewDocument(1, "a", "b"));
        col.Insert(NewDocument(2, "b"));

        col.Find(Query.EQ("tags", new BsonValue(new[] { "b" }))).Select(x => x["_id"].AsInt32).Should().Equal(2);
        col.Find(BsonExpression.Create("$.tags[*] ANY = @0", "b")).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2 });
        col.Find(Query.EQ("meta.first", "a")).Select(x => x["_id"].AsInt32).Should().Equal(1);
        col.Find(Query.GT("tags", new BsonValue(new[] { "a", "b" }))).Select(x => x["_id"].AsInt32).Should().Equal(2);
    }

    [Fact]
    public void Order_by_and_projection_should_work_on_wrapped_fields()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        col.Insert(NewDocument(1, "b"));
        col.Insert(NewDocument(2, "a"));
        col.Insert(NewDocument(3, "a", "z"));

        col.Query().OrderBy("tags").ToList().Select(x => x["_id"].AsInt32).Should().Equal(2, 3, 1);
        col.Query().OrderByDescending("meta.count").ToList().First()["_id"].AsInt32.Should().Be(3);
        col.Query().Select("{ n: COUNT($.tags[*]), first: $.tags[0] }").ToList().Select(x => x["first"].AsString).Should().Equal("b", "a", "a");
    }

    [Fact]
    public void Sql_commands_should_accept_wrapped_parameters()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        col.Insert(NewDocument(1, "a", "b"));
        col.Insert(NewDocument(2, "c"));

        var matches = db.Execute("SELECT $ FROM docs WHERE tags = @0", new BsonValue(new[] { "a", "b" })).ToList();
        var any = db.Execute("SELECT $ FROM docs WHERE tags ANY = @0", "c").ToList();
        var updated = db.Execute("UPDATE docs SET tags = @0 WHERE _id = 2", new BsonValue(new[] { "d", "e" })).ToList();
        var projected = db.Execute("SELECT { n: COUNT($.tags[*]) } FROM docs WHERE _id = 2").ToList();

        matches.Should().ContainSingle().Which["_id"].AsInt32.Should().Be(1);
        any.Should().ContainSingle().Which["_id"].AsInt32.Should().Be(2);
        updated.Should().ContainSingle().Which.AsInt32.Should().Be(1);
        projected.Should().ContainSingle().Which["n"].AsInt32.Should().Be(2);
        col.FindById(2)["tags"].AsArray.Select(x => x.AsString).Should().Equal("d", "e");
    }

    [Fact]
    public void Transactions_should_commit_and_roll_back_wrapped_documents()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");

        db.BeginTrans().Should().BeTrue();
        col.Insert(NewDocument(1, Tags));
        db.Commit().Should().BeTrue();

        db.BeginTrans().Should().BeTrue();
        col.Insert(NewDocument(2, Tags));
        db.Rollback().Should().BeTrue();

        col.Count().Should().Be(1);
        col.FindById(1)["tags"].AsArray.Count.Should().Be(3);
    }

    [Fact]
    public void Checkpoint_rebuild_and_reopen_should_preserve_wrapped_documents()
    {
        var path = Path.Combine(Path.GetTempPath(), "issue2456-" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            using (var db = new LiteDatabase(path))
            {
                var col = db.GetCollection("docs");
                col.EnsureIndex("items", "$.tags[*]");
                for (var i = 1; i <= 100; i++) col.Insert(NewDocument(i, "t" + i, "shared"));
                db.Checkpoint();
                db.Rebuild().Should().BeGreaterThanOrEqualTo(0);
                col.Count().Should().Be(100);
            }

            using (var db = new LiteDatabase(path))
            {
                var col = db.GetCollection("docs");

                col.Count().Should().Be(100);
                col.FindById(42)["tags"].AsArray.Select(x => x.AsString).Should().Equal("t42", "shared");
                col.Count(BsonExpression.Create("$.tags[*] ANY = @0", "shared")).Should().Be(100);
                col.Count(BsonExpression.Create("$.tags[*] ANY = @0", "t7")).Should().Be(1);
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void Original_issue_repros_should_no_longer_throw()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("items");
        var value = new BsonValue(new[] { "a", "b" });

        // #2456
        ((object)value.AsArray).Should().NotBeNull();
        // #2589
        value.ToString().Should().Be("[\"a\",\"b\"]");
        // #2801
        (value == new BsonArray(new BsonValue[] { "a", "b" })).Should().BeTrue();
        ((object)new BsonValue(new Dictionary<string, object> { ["a"] = 1 }).AsDocument).Should().NotBeNull();
        new BsonValue(new Dictionary<string, object> { ["a"] = 1 }).ToString().Should().Be("{\"a\":1}");
        // #1952
        col.Upsert(new BsonDocument { ["_id"] = 1, ["stringArray"] = value }).Should().BeTrue();
        col.FindById(1)["stringArray"].AsArray.Count.Should().Be(2);
    }
}
