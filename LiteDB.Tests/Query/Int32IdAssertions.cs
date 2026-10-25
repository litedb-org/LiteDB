using System.Collections.Generic;
using System.Linq;
using FluentAssertions;

namespace LiteDB.Tests.QueryTest
{
    /// <summary>Compare seeded Int32 IDs as a multiset without unordered object matching.</summary>
    internal static class Int32IdAssertions
    {
        internal static void BeEquivalentTo(IEnumerable<BsonValue> actual, IEnumerable<BsonValue> expected)
        {
            var actualIds = actual.ToArray();
            var expectedIds = expected.ToArray();
            // AsInt32 converts strings and rounds fractional numbers. These
            // fixtures create Int32 IDs, so reject coercion before projecting.
            actualIds.All(value => value is not null && value.IsInt32).Should()
                .BeTrue("actual IDs must retain their seeded BSON Int32 type");
            expectedIds.All(value => value is not null && value.IsInt32).Should()
                .BeTrue("expected IDs must retain their seeded BSON Int32 type");
            // Sorting both sequences preserves membership and multiplicity.
            // The caller independently verifies Value ordering and collation.
            actualIds.Select(value => value.AsInt32).OrderBy(value => value).Should()
                .Equal(expectedIds.Select(value => value.AsInt32).OrderBy(value => value));
        }
    }
}
