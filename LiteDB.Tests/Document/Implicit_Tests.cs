using AwesomeAssertions;
using System;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class Implicit_Tests
    {
        [Fact]
        public void BsonValue_Implicit_Convert()
        {
            int i = int.MaxValue;
            long l = long.MaxValue;
            ulong u = ulong.MaxValue;

            BsonValue bi = i;
            BsonValue bl = l;
            BsonValue bu = u;

            bi.IsInt32.Should().BeTrue();
            bl.IsInt64.Should().BeTrue();
            bu.IsDouble.Should().BeTrue();

            bi.AsInt32.Should().Be(i);
            bl.AsInt64.Should().Be(l);
            bu.AsDouble.Should().Be(u);
        }

        [Fact]
        public void BsonDocument_Inner()
        {
            var customer = new BsonDocument();
            customer["_id"] = ObjectId.NewObjectId();
            customer["Name"] = "John Doe";
            customer["CreateDate"] = DateTime.Now;
            customer["Phones"] = new BsonArray { "8000-0000", "9000-000" };
            customer["IsActive"] = true;
            customer["IsAdmin"] = new BsonValue(true);
            customer["Address"] = new BsonDocument
            {
                ["Street"] = "Av. Protasio Alves"
            };

            customer["Address"]["Number"] = "1331";

            var json = JsonSerializer.Serialize(customer);
        }
    }
}