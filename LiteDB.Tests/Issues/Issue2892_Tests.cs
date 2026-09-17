using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2892_Tests
    {
        [Fact]
        [Trait("Category", "PendingBug")]
        public void Documents_with_different_keys_have_antisymmetric_ordering()
        {
            var left = new BsonDocument { ["x"] = 1 };
            var right = new BsonDocument { ["y"] = 1 };

            var forward = left.CompareTo(right);
            var reverse = right.CompareTo(left);

            forward.Should().Be(-reverse);
            forward.Should().NotBe(0);
        }

        [Fact]
        [Trait("Category", "PendingBug")]
        public void Null_values_do_not_make_different_document_keys_equal()
        {
            var left = new BsonDocument { ["left"] = BsonValue.Null };
            var right = new BsonDocument { ["right"] = BsonValue.Null };

            left.Equals(right).Should().BeFalse();
            left.CompareTo(right).Should().NotBe(0);
        }
    }
}
