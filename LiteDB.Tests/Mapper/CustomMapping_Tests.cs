using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class CustomMappingCtor_Tests
    {
        public class UserWithCustomId
        {
            public int Key { get; }
            public string Name { get; }

            public UserWithCustomId(int key, string name)
            {
                this.Key = key;
                this.Name = name;
            }
        }

        [Fact]
        public void Custom_Ctor_With_Custom_Id()
        {
            var mapper = new BsonMapper();

            mapper.Entity<UserWithCustomId>()
              .Id(u => u.Key, false);

            var doc = new BsonDocument { ["_id"] = 10, ["name"] = "John" };

            var user = mapper.ToObject<UserWithCustomId>(doc);

            user.Key.Should().Be(10); //     Expected user.Key to be 10, but found 0.
            user.Name.Should().Be("John");
        }

        public abstract class BaseClass
        {
            [BsonId]
            public string CustomId { get; set; }

            [BsonField("CustomName")]
            public string Name { get; set; }
        }

        public class ConcreteClass : BaseClass
        {

        }

        [Fact]
        public void Custom_Id_In_Interface()
        {
            var mapper = new BsonMapper();

            var obj = new ConcreteClass { CustomId = "myid", Name = "myname" };
            var doc = mapper.Serialize(obj) as BsonDocument;
            doc["_id"].Should().NotBeNull();
            doc["_id"].Should().Be("myid");
            doc["CustomName"].Should().NotBe(BsonValue.Null);
            doc["CustomName"].Should().Be("myname");
            doc["Name"].Should().Be(BsonValue.Null);
            doc.Keys.ExpectCount(2);
        }

        public class ClassWithBsonIgnore
        {
            public int Id { get; set; }
            public string Keep { get; set; }
            [BsonIgnore]
            public string Ignore { get; set; }
        }

        [Fact]
        public void Test_BsonIgnoreAttribute()
        {
            var mapper = new BsonMapper();
            var obj = new ClassWithBsonIgnore { Id = 1, Keep = "K", Ignore = "I" };
            var doc = mapper.Serialize(obj) as BsonDocument;

            doc["_id"].Should().Be(1);
            doc["Keep"].Should().Be("K");
            doc.ContainsKey("Ignore").Should().BeFalse();
            doc.Keys.ExpectCount(2);
        }

#if NET8_0_OR_GREATER

        public class ClassWithKeyAttribute
        {
            [Key]
            public int MyKey { get; set; }
            public string Value { get; set; }
        }

        [Fact]
        public void Test_KeyAttribute_As_BsonId()
        {
            var mapper = new BsonMapper();
            var obj = new ClassWithKeyAttribute { MyKey = 123, Value = "abc" };
            var doc = mapper.Serialize(obj) as BsonDocument;

            doc["_id"].Should().Be(123);
            doc["Value"].Should().Be("abc");
            doc.Keys.ExpectCount(2);
        }

        public class ClassWithNotMappedAttribute
        {
            public int Id { get; set; }
            public string Keep { get; set; }
            [NotMapped]
            public string Ignore { get; set; }
        }

        [Fact]
        public void Test_NotMappedAttribute_As_BsonIgnore()
        {
            var mapper = new BsonMapper();
            var obj = new ClassWithNotMappedAttribute { Id = 1, Keep = "K", Ignore = "I" };
            var doc = mapper.Serialize(obj) as BsonDocument;

            doc["_id"].Should().Be(1);
            doc["Keep"].Should().Be("K");
            doc.ContainsKey("Ignore").Should().BeFalse();
            doc.Keys.ExpectCount(2);
        }
#endif
    }
}