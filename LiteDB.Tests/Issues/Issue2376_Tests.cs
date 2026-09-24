using System;
using System.Collections;
using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// #2376 - an array of a byte-backed enum is mis-detected as a raw Byte[] (CLR
/// array type-equivalence) and serialized as BsonType.Binary instead of a
/// BsonType.Array, so it no longer round-trips. An int-backed enum array works.
/// </summary>
public class Issue2376_Tests
{
    public enum ByteEnum : byte { A = 1, B = 2, C = 3 }
    public enum IntEnum { A = 1, B = 2, C = 3 }

    public class Holder
    {
        public int Id { get; set; }
        public ByteEnum[] Bytes { get; set; }
        public IntEnum[] Ints { get; set; }
    }

    public class NonGenericHolder
    {
        public IEnumerable Values { get; set; }
    }

    public class SignedByteHolder
    {
        public int Id { get; set; }
        public sbyte[] Values { get; set; }
    }

    [Fact]
    public void Byte_enum_array_serializes_as_array_not_binary()
    {
        var mapper = new BsonMapper();
        var doc = mapper.ToDocument(new Holder
        {
            Id = 1,
            Bytes = new[] { ByteEnum.A, ByteEnum.B },
            Ints = new[] { IntEnum.A, IntEnum.B }
        });

        Assert.Equal(BsonType.Array, doc["Bytes"].Type);
        Assert.Equal(BsonType.Array, doc["Ints"].Type);
    }

    [Fact]
    public void Byte_enum_array_serializes_with_enum_as_integer()
    {
        var mapper = new BsonMapper
        {
            EnumAsInteger = true
        };

        var doc = mapper.ToDocument(new Holder
        {
            Id = 1,
            Bytes = new[] { ByteEnum.A, ByteEnum.B },
            Ints = new[] { IntEnum.B, IntEnum.C }
        });

        Assert.Equal(BsonType.Array, doc["Bytes"].Type);
        Assert.Equal(1, doc["Bytes"].AsArray[0].AsInt32);
        Assert.Equal(2, doc["Bytes"].AsArray[1].AsInt32);

        var holder = mapper.Deserialize<Holder>(doc);

        Assert.Equal(new[] { ByteEnum.A, ByteEnum.B }, holder.Bytes);
        Assert.Equal(new[] { IntEnum.B, IntEnum.C }, holder.Ints);
    }

    [Fact]
    public void Byte_enum_array_round_trips_through_database()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<Holder>("h");

        col.Insert(new Holder
        {
            Id = 1,
            Bytes = new[] { ByteEnum.A, ByteEnum.C },
            Ints = new[] { IntEnum.B, IntEnum.C }
        });

        var loaded = col.FindById(1);

        Assert.Equal(new[] { ByteEnum.A, ByteEnum.C }, loaded.Bytes);
        Assert.Equal(new[] { IntEnum.B, IntEnum.C }, loaded.Ints);
    }

    // Backward compatibility: documents written by the OLD code stored a
    // byte-enum array as a Binary blob. The deserializer must still read those.
    [Fact]
    public void Legacy_binary_stored_byte_enum_array_still_deserializes()
    {
        var mapper = new BsonMapper();

        var legacy = new BsonDocument
        {
            ["_id"] = 1,
            ["Bytes"] = new BsonValue(new byte[] { 1, 3 }), // old on-disk shape (Binary)
            ["Ints"] = new BsonArray { 2, 3 }
        };

        var holder = mapper.Deserialize<Holder>(legacy);

        Assert.Equal(new[] { ByteEnum.A, ByteEnum.C }, holder.Bytes);
        Assert.Equal(new[] { IntEnum.B, IntEnum.C }, holder.Ints);
    }

    [Fact]
    public void Plain_sbyte_array_should_remain_binary()
    {
        var mapper = new BsonMapper();

        var bson = mapper.Serialize(new sbyte[] { -128, -1, 1, 127 });

        Assert.Equal(BsonType.Binary, bson.Type);
    }

    [Fact]
    public void Non_generic_collection_with_byte_enum_array_should_round_trip()
    {
        var mapper = new BsonMapper();
        var original = new NonGenericHolder
        {
            Values = new[] { ByteEnum.A, ByteEnum.B }
        };
        var document = mapper.ToDocument(original);

        NonGenericHolder loaded = null;
        var exception = Record.Exception(() => loaded = mapper.Deserialize<NonGenericHolder>(document));

        Assert.Null(exception);
        Assert.Equal(new[] { 1, 2 }, loaded.Values.Cast<object>().Select(Convert.ToInt32));
    }

    [Fact]
    public void Explicit_byte_array_contract_should_round_trip_runtime_enum_array()
    {
        ByteEnum[] enumArray = { ByteEnum.A, ByteEnum.B };
        byte[] bytes = (byte[])(Array)enumArray;
        var mapper = new BsonMapper();

        // The CLR deliberately treats byte[] and byte-backed enum arrays as
        // equivalent for this cast. The declared byte[] contract still wins.
        Assert.Equal(typeof(ByteEnum[]), bytes.GetType());

        var bson = mapper.Serialize<byte[]>(bytes);
        var result = mapper.Deserialize<byte[]>(bson);

        Assert.Equal(new byte[] { 1, 2 }, result);
    }

    [Fact]
    public void New_sbyte_rows_should_keep_the_legacy_binary_index_shape()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var raw = db.GetCollection("signed_bytes");
        sbyte[] values = { -1, 1 };
        byte[] binary = (byte[])(Array)values;

        // Existing databases store sbyte[] as Binary.
        raw.Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Values"] = new BsonValue(binary)
        });

        db.GetCollection<SignedByteHolder>("signed_bytes").Insert(new SignedByteHolder
        {
            Id = 2,
            Values = values
        });

        raw.EnsureIndex("Values");
        var matchingIds = raw.Find(Query.EQ("Values", new BsonValue(binary)))
            .Select(document => document["_id"].AsInt32)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(new[] { 1, 2 }, matchingIds);
        Assert.All(raw.FindAll(), document => Assert.Equal(BsonType.Binary, document["Values"].Type));
    }
}
