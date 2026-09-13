using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class Enum_Tests
    {
        public enum CustomerType
        {
            Potential,
            New,
            Loyal
        }
        public class Customer
        {
            public int Id { get; set; }
            public CustomerType Type { get; set; }
            public CustomerType? NullableType { get; set; }
        }

        [Fact]
        public void Enum_Convert_Into_Document()
        {
            var mapper = new BsonMapper();

            var c = new Customer { Id = 1, Type = CustomerType.Loyal };

            var doc = mapper.ToDocument(c);

            doc["Type"].AsString.Should().Be("Loyal");
            doc["NullableType"].IsNull.Should().BeTrue();

            var fromDoc = mapper.ToObject<Customer>(doc);

            fromDoc.Type.Should().Be(CustomerType.Loyal);
            fromDoc.NullableType.Should().BeNull();
        }

        [Fact]
        public void Enum_Convert_Into_Linq_Query()
        {
            var mapper = new BsonMapper();

            var c = new Customer { Id = 1, Type = CustomerType.Loyal };

            mapper.EnumAsInteger = true;

            var doc = mapper.ToDocument(c);

            doc["Type"].AsInt32.Should().Be(2);
            doc["Nullable"].IsNull.Should().BeTrue();

            // To use Eum in LINQ expressions, Enum must be integer value (should be EnumAsInteger = true)
            var expr1 = mapper.GetExpression<Customer, bool>(x => x.Type == CustomerType.Loyal);

            expr1.Parameters["p0"].AsInt32.Should().Be(2);

            var expr2 = mapper.GetExpression<Customer, bool>(x => x.NullableType.Value == CustomerType.Loyal);

            expr2.Parameters["p0"].AsInt32.Should().Be(2);

        }

        [Fact]
        public void Enum_Array_Test()
        {
            var mapper = new BsonMapper();

            mapper.EnumAsInteger = false;

            var array = new CustomerType[] { CustomerType.Potential, CustomerType.Loyal };

            var serialized1 = mapper.Serialize(array);
            var deserialized1 = mapper.Deserialize<CustomerType[]>(serialized1);
            deserialized1.Should().Equal(array);

            mapper.EnumAsInteger = true;

            var serialized2 = mapper.Serialize(array);
            var deserialized2 = mapper.Deserialize<CustomerType[]>(serialized1);
            deserialized2.Should().Equal(array);
        }
    }
}