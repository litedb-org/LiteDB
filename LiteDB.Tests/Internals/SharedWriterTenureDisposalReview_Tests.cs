#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureDisposalReview_Tests
    {
        [OneFileFact]
        public void A_new_nested_write_after_deferred_dispose_is_refused()
        {
            using var files = new SharedOneFilePublicationFixture();
            Exception nestedFailure = null;
            var nestedCount = 0;
            using (var engine = new SharedEngine(files.Settings))
            {
                var table = engine.MappedReaderTable;
                engine.Update("docs", new[] { Row(1, 0) });
                var pin = engine.Pin;
                IEnumerable<BsonDocument> Input()
                {
                    engine.Dispose();
                    nestedFailure = Record.Exception(() =>
                        nestedCount = engine.Update("docs", new[] { Row(1, 99) }));
                    yield return Row(1000, 1);
                }
                engine.Insert("admitted", Input(), BsonAutoId.Int32).Should().Be(1);
                pin.WaitReleased();
                table.Closed.Should().BeTrue();
                files.WriteIsFree();
            }
            using var cold = new LiteDatabase(files.Filename);
            cold.GetCollection("admitted").FindAll().Should().BeEquivalentTo(new[] { Row(1000, 1) },
                "the original admitted call may finish after its callback disposes the facade");
            using var assertions = new AssertionScope();
            nestedFailure.Should().BeOfType<ObjectDisposedException>(
                "a new call after Dispose returned must pass disposal admission even when it can borrow a live pin; nested update count was {0}", nestedCount);
            Verify(cold, 0);
        }
    }
}
#endif
