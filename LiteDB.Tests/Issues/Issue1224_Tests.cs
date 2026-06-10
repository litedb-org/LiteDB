using System.IO;
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

    public class Entity
    {
        public ulong Id { get; set; }
        public string Name { get; set; }
    }
}
