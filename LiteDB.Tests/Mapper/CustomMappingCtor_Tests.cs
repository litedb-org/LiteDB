using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class CustomMapping_Tests
    {
        public class User
        {
            public int Id { get; }
            public string Name { get; }

            public User(int id, string name)
            {
                this.Id = id;
                this.Name = name;
            }
        }

        public class Domain
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class MultiCtor
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string DefinedOnlyInInt32 { get; set; }

            public MultiCtor()
            {
            }

            [BsonCtor]
            public MultiCtor(int id)
            {
                this.Id = id;
                this.DefinedOnlyInInt32 = "changed";
            }

            public MultiCtor(int id, string name)
            {
                this.Id = id;
                this.Name = name;
            }
        }
        
        public class MultiCtorWithArray
        {
            public int Id { get; set; }
            public string[] StrArr { get; set; }
            public string Name { get; set; }
            public string DefinedOnlyInStrArray { get; set; }

            public MultiCtorWithArray()
            {
            }

            [BsonCtor]
            public MultiCtorWithArray(int id, string[] strarr)
            {
                this.Id = id;
                this.StrArr = strarr;
                this.DefinedOnlyInStrArray = "changed";
            }

            public MultiCtorWithArray(int id, string[] strarr, string name)
            {
                this.Id     = id;
                this.StrArr = strarr;
                this.Name   = name;
            }
        }

        public class MyClass
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public DateTimeOffset DateTimeOffset { get; set; }

            public MyClass(int id, string name, DateTimeOffset dateTimeOffset)
            {
                Id = id;
                Name = name;
                DateTimeOffset = dateTimeOffset;
            }
        }

        public class ClassByte
        {
            public byte MyByte { get; }

            [BsonCtor]
            public ClassByte(byte myByte)
            {
                MyByte = myByte;
            }
        }

        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void Custom_Ctor()
        {
            var doc = new BsonDocument { ["_id"] = 10, ["name"] = "John" };

            var user = _mapper.ToObject<User>(doc);

            user.Id.Should().Be(10);
            user.Name.Should().Be("John");
        }

        [Fact]
        public void ParameterLess_Ctor()
        {
            var doc = new BsonDocument { ["_id"] = 25, ["name"] = "numeria.com.br" };

            var domain = _mapper.ToObject<Domain>(doc);

            domain.Id.Should().Be(25);
            domain.Name.Should().Be("numeria.com.br");
        }

        [Fact]
        public void BsonCtor_Attribute()
        {
            var doc = new BsonDocument { ["_id"] = 25, ["name"] = "value-name" };

            var obj = _mapper.ToObject<MultiCtor>(doc);

            obj.Id.Should().Be(25);
            obj.Name.Should().Be("value-name");
            obj.DefinedOnlyInInt32.Should().Be("changed");
        }
        
        [Fact]
        public void BsonCtorWithArray_Attribute()
        {
            var doc = new BsonDocument { ["_id"] = 25, ["name"] = "value-name", ["strarr"] = new BsonArray() {"foo","bar"} };

            var obj = _mapper.ToObject<MultiCtorWithArray>(doc);

            obj.Id.Should().Be(25);
            obj.Name.Should().Be("value-name");
            string.Join(", ", obj.StrArr).Should().Be("foo, bar");
            obj.DefinedOnlyInStrArray.Should().Be("changed");
        }

        [Fact]
        public void Custom_Ctor_Non_Simple_Types()
        {
            var doc = new BsonDocument { ["_id"] = 1, ["Name"] = "myName", ["DateTimeOffset"] = new DateTime(2020, 01, 01).ToUniversalTime() };
            var obj = _mapper.ToObject<MyClass>(doc);

            obj.Id.Should().Be(1);
            obj.Name.Should().Be("myName");
            obj.DateTimeOffset.Should().Be(new DateTimeOffset(new DateTime(2020, 01, 01)));
        }

        [Fact]
        public void Custom_Ctor_Byte_Property()
        {
            var obj1 = new ClassByte(150);
            var doc = _mapper.ToDocument(obj1);
            var obj2 = _mapper.ToObject<ClassByte>(doc);

            obj2.MyByte.Should().Be(obj1.MyByte);
        }
    }
}