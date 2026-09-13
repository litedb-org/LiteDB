
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2712_Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Persisted_vector_update_runs_original_integrity_and_nearest_neighbor_checks(int repetition)
        {
            // The original Fact is skipped. Calling its body explicitly restores its page,
            // document and nearest-neighbor oracles without weakening any assertion.
            new QueryTest.VectorIndex_Tests().VectorIndex_HandlesVectorsSpanningMultipleDataBlocks_PersistedUpdate();
        }
    }
}
