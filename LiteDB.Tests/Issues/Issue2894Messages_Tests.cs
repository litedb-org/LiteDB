using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    // #2894 asks for failures that name the member. One readable sentence does that; a wrapper per nesting level does not.
    public class Issue2894Messages_Tests
    {
        private const string Hint = "Use BsonIgnore or RegisterType for unsupported members.";

        public class Leaf
        {
            public int Id { get; set; }

            public string Boom => throw new InvalidOperationException("sensor offline");
        }

        public class Mid
        {
            public Leaf Leaf { get; set; } = new Leaf();
        }

        public class Top
        {
            public int Id { get; set; }

            public List<Mid> Items { get; set; } = new List<Mid> { new Mid() };
        }

        public class Node
        {
            public int Id { get; set; }

            public Node Next { get; set; }
        }

        public class Money
        {
            public decimal Amount { get; set; }
        }

        public class Order
        {
            public int Id { get; set; }

            public Money Total { get; set; }
        }

        private static int Count(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

        [Fact]
        public void Nested_serialization_failure_is_one_sentence_with_the_member_path_and_the_original_error()
        {
            Action serialize = () => new BsonMapper().ToDocument(new Top { Id = 1 });

            var error = serialize.Should().Throw<LiteException>().Which;

            error.Message.Should().Be("Error serializing 'Top.Items > Mid.Leaf > Leaf.Boom': sensor offline. " + Hint);
            error.ErrorCode.Should().Be(LiteException.MAPPING_ERROR);
            error.InnerException.Should().BeOfType<InvalidOperationException>("the original error must be one step away, not three");
            error.InnerException.Message.Should().Be("sensor offline");
        }

        [Fact]
        public void Circular_reference_reports_the_loop_once_instead_of_once_per_level()
        {
            var node = new Node { Id = 1 };
            node.Next = node;
            Action serialize = () => new BsonMapper().ToDocument(node);

            var error = serialize.Should().Throw<LiteException>().Which;

            error.ErrorCode.Should().Be(LiteException.DOCUMENT_MAX_DEPTH);
            Count(error.Message, "Error serializing").Should().Be(1);
            error.Message.Should().MatchRegex(@"^Error serializing 'Node\.Next \(x\d+\) > Node\.\w+': ").And.Contain("circular references");
            error.Message.Should().NotContain(Hint, "the depth error already says what to do");
            error.Message.Length.Should().BeLessThan(300);
            error.InnerException.Should().BeOfType<LiteException>().Which.InnerException.Should().BeNull();
        }

        [Fact]
        public void Deserialization_failure_names_the_member_and_the_stored_type()
        {
            var doc = new BsonDocument { ["_id"] = 1, ["Total"] = new BsonDocument { ["Amount"] = "abc" } };
            Action deserialize = () => new BsonMapper().ToObject<Order>(doc);

            var error = deserialize.Should().Throw<LiteException>().Which;

            error.ErrorCode.Should().Be(LiteException.MAPPING_ERROR);
            error.Message.Should().StartWith("Error deserializing 'Order.Total > Money.Amount' from String: ");
            Count(error.Message, "Error deserializing").Should().Be(1);
            error.InnerException.Should().NotBeNull().And.NotBeOfType<LiteException>();
        }

        [Fact]
        public void Deserialization_of_matching_data_is_untouched()
        {
            var doc = new BsonDocument { ["_id"] = 1, ["Total"] = new BsonDocument { ["Amount"] = 12.5m } };

            new BsonMapper().ToObject<Order>(doc).Total.Amount.Should().Be(12.5m);
        }
    }
}
