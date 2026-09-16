using System;
using System.Collections;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class BsonObjectConstructor_Tests
    {
        [Fact]
        public void Collections_fail_at_construction_with_conversion_guidance()
        {
            object[] collections =
            {
                new BsonArray(1, 2), new BsonDocument { ["a"] = 1 },
                new[] { 1, 2 }, new List<int> { 1, 2 }, new Hashtable { ["a"] = 1 },
                Enumerate(), new Dictionary<string, object> { ["a"] = 1 }
            };
            foreach (var collection in collections)
            {
                Action construct = () => new BsonValue(collection);
                construct.Should().Throw<ArgumentException>().WithParameterName("value")
                    .WithMessage("*BsonArray*BsonDocument*BsonMapper*");
            }
        }

        [Fact]
        public void Boxed_scalars_binary_and_vectors_retain_their_types_and_values()
        {
            object[] values =
            {
                null, 12, 12L, 1.5, 1.5m, "text", new byte[] { 1, 2 },
                new ObjectId(), Guid.NewGuid(), true, DateTime.UtcNow,
                new float[] { 1, 2 }, new BsonValue(42)
            };
            BsonType[] types =
            {
                BsonType.Null, BsonType.Int32, BsonType.Int64, BsonType.Double,
                BsonType.Decimal, BsonType.String, BsonType.Binary, BsonType.ObjectId,
                BsonType.Guid, BsonType.Boolean, BsonType.DateTime, BsonType.Vector, BsonType.Int32
            };
            for (var i = 0; i < values.Length; i++)
            {
                var wrapped = new BsonValue(values[i]);
                wrapped.Type.Should().Be(types[i]);
                if (values[i] is DateTime date)
                    wrapped.AsDateTime.Should().Be(new BsonValue(date).AsDateTime);
                else if (values[i] is BsonValue bson)
                    wrapped.RawValue.Should().Be(bson.RawValue);
                else
                    wrapped.RawValue.Should().Be(values[i]);
            }
        }

        private static IEnumerable<int> Enumerate()
        {
            yield return 1;
            throw new InvalidOperationException("Rejected collections must not be enumerated.");
        }
    }
}
