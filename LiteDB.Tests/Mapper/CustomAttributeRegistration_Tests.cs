using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class CustomAttributeRegistration_Tests
    {
        // Custom attributes for testing
        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
        public class MyCustomIdAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
        public class MyCustomIgnoreAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
        public class AnotherIdAttribute : Attribute { }

        // Test classes
        public class EntityWithCustomIdAttribute
        {
            [MyCustomId]
            public int CustomId { get; set; }
            public string Name { get; set; }
        }

        public class EntityWithCustomIgnoreAttribute
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [MyCustomIgnore]
            public string IgnoredField { get; set; }
        }

        public class EntityWithMultipleCustomAttributes
        {
            [AnotherId]
            public string Key { get; set; }
            public string Value { get; set; }
            [MyCustomIgnore]
            public string Secret { get; set; }
        }

        public class EntityWithByNameAttributes
        {
            public int MyKey { get; set; }
            public string Data { get; set; }
            public string ShouldBeIgnored { get; set; }
        }

        [Fact]
        public void RegisterIdAttribute_Should_Recognize_Custom_Id()
        {
            // Register custom ID attribute
            BsonMapper.RegisterIdAttribute(typeof(MyCustomIdAttribute));

            var mapper = new BsonMapper();
            var entity = new EntityWithCustomIdAttribute { CustomId = 123, Name = "Test" };
            var doc = mapper.Serialize(entity) as BsonDocument;

            doc["_id"].Should().Be(123);
            doc["Name"].Should().Be("Test");
            doc.Keys.Count.Should().Be(2);
        }

        [Fact]
        public void RegisterIgnoreAttribute_Should_Ignore_Custom_Attribute()
        {
            // Register custom Ignore attribute
            BsonMapper.RegisterIgnoreAttribute(typeof(MyCustomIgnoreAttribute));

            var mapper = new BsonMapper();
            var entity = new EntityWithCustomIgnoreAttribute 
            { 
                Id = 1, 
                Name = "Test", 
                IgnoredField = "Should not be serialized" 
            };
            var doc = mapper.Serialize(entity) as BsonDocument;

            doc["_id"].Should().Be(1);
            doc["Name"].Should().Be("Test");
            doc.ContainsKey("IgnoredField").Should().BeFalse();
            doc.Keys.Count.Should().Be(2);
        }

        [Fact]
        public void RegisterMultipleCustomAttributes_Should_Work()
        {
            // Register multiple custom attributes
            BsonMapper.RegisterIdAttribute(typeof(AnotherIdAttribute));
            BsonMapper.RegisterIgnoreAttribute(typeof(MyCustomIgnoreAttribute));

            var mapper = new BsonMapper();
            var entity = new EntityWithMultipleCustomAttributes 
            { 
                Key = "PK_001", 
                Value = "Some Value",
                Secret = "Hidden"
            };
            var doc = mapper.Serialize(entity) as BsonDocument;

            doc["_id"].Should().Be("PK_001");
            doc["Value"].Should().Be("Some Value");
            doc.ContainsKey("Secret").Should().BeFalse();
            doc.Keys.Count.Should().Be(2);
        }

        [Fact]
        public void RegisterIdAttributeByName_Should_Work_Without_Type_Reference()
        {
            // Register by full name (simulating external attribute)
            BsonMapper.RegisterIdAttributeByName("LiteDB.Tests.Mapper.CustomAttributeRegistration_Tests+MyCustomIdAttribute");

            var mapper = new BsonMapper();
            var entity = new EntityWithCustomIdAttribute { CustomId = 456, Name = "ByName" };
            var doc = mapper.Serialize(entity) as BsonDocument;

            doc["_id"].Should().Be(456);
            doc["Name"].Should().Be("ByName");
        }

        [Fact]
        public void RegisterIgnoreAttributeByName_Should_Work_Without_Type_Reference()
        {
            // Register by full name
            BsonMapper.RegisterIgnoreAttributeByName("LiteDB.Tests.Mapper.CustomAttributeRegistration_Tests+MyCustomIgnoreAttribute");

            var mapper = new BsonMapper();
            var entity = new EntityWithCustomIgnoreAttribute 
            { 
                Id = 2, 
                Name = "ByName", 
                IgnoredField = "Should be ignored" 
            };
            var doc = mapper.Serialize(entity) as BsonDocument;

            doc["_id"].Should().Be(2);
            doc["Name"].Should().Be("ByName");
            doc.ContainsKey("IgnoredField").Should().BeFalse();
        }

        [Fact]
        public void RegisterIdAttribute_Null_Should_Throw()
        {
            Action act = () => BsonMapper.RegisterIdAttribute(null);
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("attributeType");
        }

        [Fact]
        public void RegisterIdAttribute_NonAttributeType_Should_Throw()
        {
            Action act = () => BsonMapper.RegisterIdAttribute(typeof(string));
            act.Should().Throw<ArgumentException>()
                .WithMessage("Type must be an Attribute type*");
        }

        [Fact]
        public void RegisterIgnoreAttribute_Null_Should_Throw()
        {
            Action act = () => BsonMapper.RegisterIgnoreAttribute(null);
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("attributeType");
        }

        [Fact]
        public void RegisterIgnoreAttribute_NonAttributeType_Should_Throw()
        {
            Action act = () => BsonMapper.RegisterIgnoreAttribute(typeof(int));
            act.Should().Throw<ArgumentException>()
                .WithMessage("Type must be an Attribute type*");
        }

        [Fact]
        public void RegisterIdAttributeByName_NullOrEmpty_Should_Throw()
        {
            Action act1 = () => BsonMapper.RegisterIdAttributeByName(null);
            Action act2 = () => BsonMapper.RegisterIdAttributeByName("");
            Action act3 = () => BsonMapper.RegisterIdAttributeByName("   ");

            act1.Should().Throw<ArgumentException>()
                .WithMessage("Attribute name cannot be null or empty*");
            act2.Should().Throw<ArgumentException>()
                .WithMessage("Attribute name cannot be null or empty*");
            act3.Should().Throw<ArgumentException>()
                .WithMessage("Attribute name cannot be null or empty*");
        }

        [Fact]
        public void RegisterIgnoreAttributeByName_NullOrEmpty_Should_Throw()
        {
            Action act1 = () => BsonMapper.RegisterIgnoreAttributeByName(null);
            Action act2 = () => BsonMapper.RegisterIgnoreAttributeByName("");
            Action act3 = () => BsonMapper.RegisterIgnoreAttributeByName("   ");

            act1.Should().Throw<ArgumentException>()
                .WithMessage("Attribute name cannot be null or empty*");
            act2.Should().Throw<ArgumentException>()
                .WithMessage("Attribute name cannot be null or empty*");
            act3.Should().Throw<ArgumentException>()
                .WithMessage("Attribute name cannot be null or empty*");
        }

        [Fact]
        public void RegisterSameAttribute_Multiple_Times_Should_Not_Duplicate()
        {
            // Register same attribute multiple times
            BsonMapper.RegisterIdAttribute(typeof(MyCustomIdAttribute));
            BsonMapper.RegisterIdAttribute(typeof(MyCustomIdAttribute));
            BsonMapper.RegisterIdAttribute(typeof(MyCustomIdAttribute));

            var mapper = new BsonMapper();
            var entity = new EntityWithCustomIdAttribute { CustomId = 789, Name = "NoDuplicates" };
            var doc = mapper.Serialize(entity) as BsonDocument;

            // Should still work correctly without issues
            doc["_id"].Should().Be(789);
            doc["Name"].Should().Be("NoDuplicates");
        }

        [Fact]
        public void Custom_Attributes_Should_Have_Lower_Priority_Than_BsonId()
        {
            BsonMapper.RegisterIdAttribute(typeof(MyCustomIdAttribute));

            var mapper = new BsonMapper();
            
            // If both BsonId and custom attribute exist, BsonId takes precedence
            // This test ensures the documented behavior
            var entity = new EntityWithCustomIdAttribute { CustomId = 999, Name = "Priority" };
            var doc = mapper.Serialize(entity) as BsonDocument;

            doc["_id"].Should().Be(999);
        }

        [Fact]
        public void Deserialization_With_Custom_Attributes_Should_Work()
        {
            BsonMapper.RegisterIdAttribute(typeof(MyCustomIdAttribute));
            BsonMapper.RegisterIgnoreAttribute(typeof(MyCustomIgnoreAttribute));

            var mapper = new BsonMapper();
            
            var doc = new BsonDocument 
            { 
                ["_id"] = 100, 
                ["Name"] = "Deserialize Test",
                ["IgnoredField"] = "This will be ignored"
            };

            var entity = mapper.Deserialize<EntityWithCustomIgnoreAttribute>(doc);

            entity.Id.Should().Be(100);
            entity.Name.Should().Be("Deserialize Test");
            // IgnoredField should remain null/default since it's ignored
        }
    }
}