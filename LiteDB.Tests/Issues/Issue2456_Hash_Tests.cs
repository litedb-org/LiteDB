using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2456_Hash_Tests
{
    [Fact]
    public void Wrapped_collections_should_compare_equal_and_share_hash_codes_with_their_adapters()
    {
        var array = new BsonValue(new[] { "a" });
        var document = new BsonValue(new Dictionary<string, BsonValue> { ["x"] = 1 });

        array.Should().Be(array.AsArray);
        array.GetHashCode().Should().Be(array.AsArray.GetHashCode());
        document.Should().Be(document.AsDocument);
        document.GetHashCode().Should().Be(document.AsDocument.GetHashCode());
    }

    [Fact]
    public void Independently_wrapped_equal_collections_should_support_hash_lookup()
    {
        var firstArray = new BsonValue((object)new BsonValue[] { 1, new byte[] { 2, 3 } });
        var equalArray = new BsonValue((object)new BsonValue[] { 1L, new byte[] { 2, 3 } });
        var firstDocument = new BsonValue(new Dictionary<string, BsonValue>
        {
            ["number"] = 1,
            ["payload"] = new byte[] { 2, 3 }
        });
        var equalDocument = new BsonValue(new Dictionary<string, BsonValue>
        {
            ["PAYLOAD"] = new byte[] { 2, 3 },
            ["NUMBER"] = 1L
        });

        new HashSet<BsonValue> { firstArray }.Should().Contain(equalArray);
        new HashSet<BsonValue> { firstDocument }.Should().Contain(equalDocument);
    }
}
