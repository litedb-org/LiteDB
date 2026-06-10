#if !NETFRAMEWORK
using System;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1929_Tests
    {
        [Fact]
        public void FindById_Should_Deserialize_System_Index_Property()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var collection = db.GetCollection<Person>("people");
            var person = new Person
            {
                Name = "Alex",
                Selection = System.Index.FromEnd(2)
            };

            collection.Insert(person);

            var result = collection.FindById(person.Id);

            Assert.NotNull(result);
            Assert.Equal(person.Name, result.Name);
            Assert.Equal(person.Selection, result.Selection);
        }

        [Fact]
        public void Deserialize_Should_Handle_System_Index_With_Object_Type_Discriminator()
        {
            var mapper = new BsonMapper();
            var expected = System.Index.FromEnd(3);

            var value = mapper.Serialize(typeof(object), expected);
            var result = mapper.Deserialize(typeof(object), value);

            var index = Assert.IsType<System.Index>(result);
            Assert.Equal(expected, index);
        }

        [Fact]
        public void Deserialize_Should_Handle_System_Index_With_Lower_Case_Delimiter()
        {
            var mapper = new BsonMapper().UseLowerCaseDelimiter();
            var expected = System.Index.FromEnd(4);

            var value = mapper.Serialize(expected);
            var result = mapper.Deserialize(typeof(System.Index), value);

            var index = Assert.IsType<System.Index>(result);
            Assert.Equal(expected, index);
        }

        public class Person
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();

            public string Name { get; set; }

            public System.Index Selection { get; set; }
        }
    }
}
#endif
