using System;
using System.IO;
using System.Linq;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue1224_Compatibility_Tests
{
    private const ulong LegacyValue = (1UL << 60) + 12345UL;

    [Fact]
    public void Fractional_FindById_should_not_match_an_integer_id()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("entities");

        col.Insert(new Entity { Id = 2UL, Name = "integer" });

        Assert.Null(col.FindById(new BsonValue(1.9)));
    }

    [Fact]
    public void Linq_equality_should_find_a_legacy_ulong_id()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("entities");

        db.GetCollection("entities").Insert(LegacyDocument(LegacyValue));

        var found = col.Find(x => x.Id == LegacyValue).SingleOrDefault();

        Assert.NotNull(found);
        Assert.Equal("legacy", found.Name);
    }

    [Fact]
    public void Indexed_and_scan_equality_should_find_a_legacy_ulong_value()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<ValueEntity>("entities");

        col.EnsureIndex(x => x.Value);
        db.GetCollection("entities").Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Value"] = new BsonValue((double)LegacyValue),
            ["Name"] = "legacy"
        });

        var indexed = col.Find(x => x.Value == LegacyValue).SingleOrDefault();
        col.DropIndex("Value");
        var unindexed = col.Find(x => x.Value == LegacyValue).SingleOrDefault();

        Assert.NotNull(indexed);
        Assert.Equal("legacy", indexed.Name);
        Assert.NotNull(unindexed);
        Assert.Equal("legacy", unindexed.Name);
    }

    [Fact]
    public void Delete_should_remove_a_legacy_ulong_id()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var raw = db.GetCollection("entities");

        raw.Insert(LegacyDocument(LegacyValue));

        Assert.True(raw.Delete(LegacyValue));
        Assert.Equal(0, raw.Count());
    }

    [Fact]
    public void Update_should_modify_a_legacy_ulong_id_without_changing_its_key()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("entities");
        var raw = db.GetCollection("entities");
        var legacyId = new BsonValue((double)LegacyValue);

        raw.Insert(LegacyDocument(LegacyValue));

        var updated = col.Update(new Entity { Id = LegacyValue, Name = "updated" });
        var stored = raw.FindById(legacyId);

        Assert.True(updated);
        Assert.NotNull(stored);
        Assert.True(stored["_id"].IsDouble);
        Assert.Equal("updated", stored["Name"].AsString);
    }

    [Fact]
    public void Upsert_should_update_a_legacy_ulong_id_without_inserting_a_copy()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("entities");
        var raw = db.GetCollection("entities");

        raw.Insert(LegacyDocument(LegacyValue));

        var inserted = col.Upsert(new Entity { Id = LegacyValue, Name = "updated" });

        Assert.False(inserted);
        Assert.Equal(1, raw.Count());
        Assert.Equal("updated", raw.FindById(LegacyValue)["Name"].AsString);
    }

    [Fact]
    public void Unique_index_should_reject_an_exact_ulong_that_matches_a_legacy_value()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<ValueEntity>("entities");

        col.EnsureIndex(x => x.Value, true);
        db.GetCollection("entities").Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Value"] = new BsonValue((double)LegacyValue)
        });

        Assert.Throws<LiteException>(() => col.Insert(new ValueEntity
        {
            Id = 2,
            Value = LegacyValue,
            Name = "exact"
        }));
    }

    [Fact]
    public void Legacy_ulong_lookup_should_survive_checkpoint_and_reopen()
    {
        using var tempFile = new TempFile();

        using (var db = new LiteDatabase(tempFile.Filename))
        {
            db.GetCollection("entities").Insert(LegacyDocument(LegacyValue));
            db.Checkpoint();
        }

        using (var db = new LiteDatabase(tempFile.Filename))
        {
            var found = db.GetCollection<Entity>("entities")
                .Find(x => x.Id == LegacyValue)
                .SingleOrDefault();

            Assert.NotNull(found);
            Assert.Equal("legacy", found.Name);
        }
    }

    [Fact]
    public void Invalid_legacy_double_ulong_values_should_fail_clearly()
    {
        var mapper = new BsonMapper();

        Assert.Throws<OverflowException>(() => mapper.Deserialize<ulong>(new BsonValue(double.PositiveInfinity)));
        Assert.Throws<OverflowException>(() => mapper.Deserialize<ulong>(new BsonValue(double.NaN)));
        Assert.Throws<OverflowException>(() => mapper.Deserialize<ulong>(new BsonValue(-0.1d)));
        Assert.Throws<OverflowException>(() => mapper.Deserialize<ulong>(new BsonValue(Math.Pow(2, 65))));
    }

    private static BsonDocument LegacyDocument(ulong id)
    {
        return new BsonDocument
        {
            ["_id"] = new BsonValue((double)id),
            ["Name"] = "legacy"
        };
    }

    public class ValueEntity
    {
        public int Id { get; set; }
        public ulong Value { get; set; }
        public string Name { get; set; }
    }

    public class Entity
    {
        public ulong Id { get; set; }
        public string Name { get; set; }
    }
}
