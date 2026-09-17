using System;
using System.Collections.Generic;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Issues
{
    public class Issue2891_Tests
    {
        [Fact]
        [Trait("Category", "PendingBug")]
        public void Collection_snapshot_survives_a_header_change_while_it_is_enumerated()
        {
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var header = new HeaderPage(buffer, 0);
            header.InsertCollection("first", 1);
            header.InsertCollection("second", 2);

            using var iterator = header.GetCollections().GetEnumerator();
            iterator.MoveNext().Should().BeTrue();
            var observed = new List<string> { iterator.Current.Key };

            lock (header)
            {
                header.InsertCollection("created-concurrently", 3);
            }

            Action finishSnapshot = () =>
            {
                while (iterator.MoveNext()) observed.Add(iterator.Current.Key);
            };

            finishSnapshot.Should().NotThrow<InvalidOperationException>();
            observed.Should().BeEquivalentTo("first", "second");
        }
    }
}
