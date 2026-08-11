using System;
using System.Collections.Generic;
using System.IO;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// #1224 - the implicit operator BsonValue(ulong) casts to Double, producing a
/// BsonType.Double value (wrong type) and losing precision above 2^53. Every
/// values that fit in the signed range should remain Int64 for compatibility.
/// Values above Int64.MaxValue need a distinct, lossless BSON representation;
/// otherwise ulong.MaxValue aliases the signed key -1.
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

    [Fact]
    public void Raw_FindById_should_still_find_a_legacy_ulong_id_after_upgrade()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var raw = db.GetCollection("entities");
        ulong id = (1UL << 60) + 12345UL;

        // LiteDB 5.0.21 stored ulong IDs as BSON Double values.
        raw.Insert(new BsonDocument
        {
            ["_id"] = new BsonValue((double)id),
            ["Name"] = "written by 5.0.21"
        });

        var found = raw.FindById(id);

        Assert.NotNull(found);
        Assert.Equal("written by 5.0.21", found["Name"].AsString);
    }

    [Fact]
    public void Ulong_max_value_should_not_be_the_same_BSON_key_as_signed_minus_one()
    {
        BsonValue unsigned = ulong.MaxValue;
        BsonValue signed = -1L;

        Assert.Equal(BsonType.Decimal, unsigned.Type);
        Assert.NotEqual(signed, unsigned);
    }

    [Fact]
    public void Legacy_double_outside_the_ulong_range_should_fail_clearly()
    {
        var mapper = new BsonMapper();
        var roundedTwoToThePowerOf64 = new BsonValue(Math.Pow(2, 64));

        // ulong.MaxValue was rounded to exactly 2^64 by the legacy Double format.
        // Silently turning that out-of-range value into 0 or ulong.MaxValue is unsafe.
        Assert.Throws<OverflowException>(() => mapper.Deserialize<ulong>(roundedTwoToThePowerOf64));
    }

    [Fact]
    public void Deserializing_an_in_range_double_to_ulong_should_keep_mapper_rounding()
    {
        var mapper = new BsonMapper();

        var result = mapper.Deserialize<ulong>(new BsonValue(1.9));

        Assert.Equal(2UL, result);
    }

    [Fact]
    public void Legacy_ulong_FindById_fallback_should_use_one_query_snapshot()
    {
        var engine = new CountingEmptyEngine();
        var collection = new LiteCollection<Entity>(
            "entities",
            BsonAutoId.ObjectId,
            engine,
            new BsonMapper());

        collection.FindById(ulong.MaxValue);

        Assert.Equal(1, engine.QueryCount);
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

    private sealed class CountingEmptyEngine : ILiteEngine
    {
        public int QueryCount { get; private set; }

        public IBsonDataReader Query(string collection, Query query)
        {
            QueryCount++;
            return new BsonDataReader();
        }

        public int Checkpoint() => throw new NotSupportedException();
        public long Rebuild(RebuildOptions options) => throw new NotSupportedException();
        public bool BeginTrans() => throw new NotSupportedException();
        public bool Commit() => throw new NotSupportedException();
        public bool Rollback() => throw new NotSupportedException();
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => throw new NotSupportedException();
        public int Update(string collection, IEnumerable<BsonDocument> docs) => throw new NotSupportedException();
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => throw new NotSupportedException();
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => throw new NotSupportedException();
        public int Delete(string collection, IEnumerable<BsonValue> ids) => throw new NotSupportedException();
        public int DeleteMany(string collection, BsonExpression predicate) => throw new NotSupportedException();
        public bool DropCollection(string name) => throw new NotSupportedException();
        public bool RenameCollection(string name, string newName) => throw new NotSupportedException();
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => throw new NotSupportedException();
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, LiteDB.Vector.VectorIndexOptions options) => throw new NotSupportedException();
        public bool DropIndex(string collection, string name) => throw new NotSupportedException();
        public BsonValue Pragma(string name) => throw new NotSupportedException();
        public bool Pragma(string name, BsonValue value) => throw new NotSupportedException();
        public void Dispose()
        {
        }
    }
}
