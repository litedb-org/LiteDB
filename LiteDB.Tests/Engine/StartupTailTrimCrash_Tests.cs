using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Startup cuts an incomplete trailing page off the data file, and off a legacy
    /// (unchecksummed) WAL. A process dying at either trim boundary must leave files
    /// that the next open recovers completely, also after a second death there.
    /// </summary>
    [Collection("PromotionPowerLoss")] // EngineState.SimulateProcessCrash is process-wide
    public class StartupTailTrimCrash_Tests
    {
        private const string BeforeTrim = "startup-before-tail-trim";
        private const string AfterTrim = "startup-after-tail-trim";

        public static IEnumerable<object[]> Interruptions()
        {
            // legacy WAL, boundary, occurrence, whether the next open reaches the boundary again
            var rows = new[]
            {
                new object[] { false, BeforeTrim, 1, true },  // the data tail is still there
                new object[] { false, AfterTrim, 1, false },  // the data tail is gone: nothing to cut
                new object[] { true, BeforeTrim, 1, true },   // before the data trim
                new object[] { true, BeforeTrim, 2, true },   // data trimmed, before the WAL trim
                new object[] { true, AfterTrim, 1, true },    // data trimmed, the WAL tail remains
                new object[] { true, AfterTrim, 2, false },   // both trimmed
            };
            foreach (var password in new[] { null, "secret" })
            foreach (var processDeath in new[] { true, false })
            foreach (var row in rows)
                yield return new[] { row[0], password, row[1], row[2], row[3], processDeath };
        }

        [Theory]
        [MemberData(nameof(Interruptions))]
        public void InterruptedTailTrim_RecoversEveryCommittedDocumentRepeatedly(bool legacy, string password,
            string boundary, int occurrence, bool reachedAgain, bool processDeath)
        {
            // processDeath restores the files captured at the boundary; otherwise the
            // interrupted open's own error-close cleanup is kept (exception model).
            using var fixture = new StartupTailTrimFixture(legacy, password);

            fixture.OpenInterruptedAt(boundary, occurrence, processDeath)
                .Should().BeTrue("the first open must reach {0} #{1}", boundary, occurrence);
            fixture.OpenInterruptedAt(boundary, 1, processDeath)
                .Should().Be(reachedAgain, "a second open dies at {0} whenever a tail is still left to cut", boundary);

            fixture.VerifyCleanOpen();
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "secret")]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public void UninterruptedOpen_CutsEachIncompleteTailOnce(bool legacy, string password)
        {
            // Positive control: the fixture really has a tail behind every boundary above.
            using var fixture = new StartupTailTrimFixture(legacy, password);
            var expected = legacy
                ? new[] { BeforeTrim, AfterTrim, BeforeTrim, AfterTrim }
                : new[] { BeforeTrim, AfterTrim };

            fixture.OpenRecordingTrims(false).Should().Equal(expected);

            fixture.VerifyCleanOpen();
        }
    }
}
