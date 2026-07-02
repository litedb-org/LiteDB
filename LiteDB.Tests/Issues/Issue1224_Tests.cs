using System.IO;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// #1224 - the implicit operator BsonValue(ulong) casts to Double, producing a
/// BsonType.Double value (wrong type) and losing precision above 2^53. Every
/// other ulong path in the library stores ulong as Int64 (BsonMapper). The
/// operator must do the same, and the reverse operator must read it back.
/// </summary>
public class Issue1224_Tests
{
    [Fact]
    public void Implicit_ulong_operator_produces_Int64_not_Double()
    {
        ulong value = 42UL;

        BsonValue bson = value; // implicit operator BsonValue(UInt64)

        Assert.Equal(BsonType.Int64, bson.Type);
        Assert.Equal(42L, bson.AsInt64);
    }

    [Fact]
    public void Implicit_ulong_operator_preserves_full_64bit_precision()
    {
        ulong value = (1UL << 53) + 1UL; // 9007199254740993 - not representable as double

        BsonValue bson = value;

        Assert.Equal(unchecked((long)value), bson.AsInt64);

        ulong roundTrip = (ulong)bson; // implicit operator UInt64(BsonValue)
        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void Implicit_ulong_operator_reads_legacy_double_above_int64_max()
    {
        ulong value = 1UL << 63;
        BsonValue legacy = new BsonValue((double)value);

        ulong roundTrip = legacy;

        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void Mapper_deserializes_legacy_double_ulong_above_int64_max()
    {
        var mapper = new BsonMapper();
        ulong value = 1UL << 63;
        var legacy = new BsonValue((double)value);

        ulong roundTrip = mapper.Deserialize<ulong>(legacy);

        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void FindById_with_large_ulong_key_returns_document()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("entities");

        ulong id = (1UL << 60) + 12345UL; // far above 2^53

        col.Insert(new Entity { Id = id, Name = "x" });

        var found = col.FindById(id); // ulong -> BsonValue via implicit operator

        Assert.NotNull(found);
        Assert.Equal(id, found.Id);
        Assert.Equal("x", found.Name);
    }

    [Fact]
    public void FindById_with_high_bit_ulong_key_round_trips_after_reopen()
    {
        using var tempFile = new TempFile();
        ulong id = ulong.MaxValue;

        using (var db = new LiteDatabase(tempFile.Filename))
        {
            var col = db.GetCollection<Entity>("entities");

            col.Insert(new Entity { Id = id, Name = "high-bit" });
            db.Checkpoint();
        }

        using (var db = new LiteDatabase(tempFile.Filename))
        {
            var found = db.GetCollection<Entity>("entities").FindById(id);

            Assert.NotNull(found);
            Assert.Equal(id, found.Id);
            Assert.Equal("high-bit", found.Name);
        }
    }

    [Fact]
    public void FindById_with_legacy_double_ulong_key_returns_document()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Entity>("entities");
        ulong id = (1UL << 60) + 12345UL;
        var legacyId = new BsonValue((double)id);

        db.GetCollection("entities").Insert(new BsonDocument
        {
            ["_id"] = legacyId,
            ["Name"] = "legacy"
        });

        var found = col.FindById(id);

        Assert.NotNull(found);
        Assert.Equal("legacy", found.Name);
        // The old double representation had already lost precision; compat can only read the stored rounded key.
        Assert.Equal(unchecked((ulong)legacyId.AsInt64), found.Id);
    }

    [Fact]
    public void Legacy_high_bit_double_ulong_field_round_trips_through_database()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<LegacyValueEntity>("legacy_values");
        ulong value = 1UL << 63;

        db.GetCollection("legacy_values").Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Value"] = new BsonValue((double)value)
        });

        var found = col.FindById(1);

        Assert.NotNull(found);
        Assert.Equal(value, found.Value);
    }

    [Fact]
    public void FindById_with_long_key_does_not_match_legacy_ulong_double_key()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<LongEntity>("longs");
        ulong legacyUlong = (1UL << 60) + 12345UL;

        db.GetCollection("longs").Insert(new BsonDocument
        {
            ["_id"] = new BsonValue((double)legacyUlong),
            ["Name"] = "legacy"
        });

        var found = col.FindById(unchecked((long)legacyUlong));

        Assert.Null(found);
    }

    public class Entity
    {
        public ulong Id { get; set; }
        public string Name { get; set; }
    }

    public class LongEntity
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    public class LegacyValueEntity
    {
        public int Id { get; set; }
        public ulong Value { get; set; }
    }
}
