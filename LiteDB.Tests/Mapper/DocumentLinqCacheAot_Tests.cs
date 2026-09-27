using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class DocumentLinqCacheAot_Tests
    {
        [Fact]
        public void Cached_document_bindings_reject_application_objects_after_native_values()
        {
            var mapper = new BsonMapper();
            object captured = 12;
            Expression<Func<BsonDocument, object>> expression = document => captured;

            mapper.GetExpression(expression).Execute().Single().AsInt32.Should().Be(12);
            captured = "changed";
            mapper.GetExpression(expression).Execute().Single().AsString.Should().Be("changed");
            captured = new ApplicationValue { Number = 42 };
            Action bind = () => mapper.GetExpression(expression);
            bind.Should().Throw<NotSupportedException>();
            captured = 21;
            mapper.GetExpression(expression).Execute().Single().AsInt32.Should().Be(21);
        }

        [Fact]
        public void Cached_runtime_bindings_keep_application_object_mapping()
        {
            var mapper = new BsonMapper();
            var captured = new ApplicationValue { Number = 12 };
            Expression<Func<ApplicationValue, object>> expression = document => captured;

            mapper.GetExpression(expression).Execute().Single()["Number"].AsInt32.Should().Be(12);
            captured = new ApplicationValue { Number = 21 };
            mapper.GetExpression(expression).Execute().Single()["Number"].AsInt32.Should().Be(21);
        }

        public sealed class ApplicationValue
        {
            public int Number { get; set; }
        }
    }
}
