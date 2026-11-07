#if NET8_0_OR_GREATER
using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureCoordinatedAdmission_Tests
    {
        [OneFileTheory]
        [InlineData(null, false)] [InlineData(null, true)]
        [InlineData("secret", false)] [InlineData("secret", true)]
        public void Pin_handoff_after_admission_sample_reads_fresh_commits_and_preserves_old_snapshot(string password, bool legacy)
        {
            using var files = new SharedOneFilePublicationFixture(password, legacy);
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.Update("docs", new[] { Row(1, 0) });
                SharedOneFileWarmReads_Tests.Warm(engine, db);
                using var old = engine.Query("docs", new Query());
                old.Read().Should().BeTrue();
                var pin = engine.Pin;
                pin.Should().NotBeNull();
                pin.IsIdle.Should().BeTrue();
                var reached = 0;
                engine.CoordinationStage = stage =>
                {
                    if (stage != "pin-sampled") return;
                    engine.CoordinationStage = null;
                    reached++;
                    // The holder clears _pin between the admission's null check
                    // and its idle probe. Retain the sampled pin's lifetime only.
                    pin.RequestRelease(force: false);
                    pin.WaitReleased();
                    engine.Pin.Should().BeNull();
                    using var writer = new SharedEngine(files.Settings);
                    writer.Update("docs", new[] { Row(1, 7) }).Should().Be(1);
                    writer.Checkpoint();
                };
                db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 7));
                reached.Should().Be(1);
                Verify(db, 7);
                var rows = new[] { old.Current.AsDocument }.Concat(ReadRemaining(old)).ToArray();
                rows.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                engine.Update("docs", new[] { Row(1, 8) }).Should().Be(1);
                Verify(db, 8);
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(new LiteDB.Engine.LiteEngine(files.Settings));
                Verify(cold, 8);
            }
        }

        private static System.Collections.Generic.IEnumerable<BsonDocument> ReadRemaining(IBsonDataReader reader)
        {
            while (reader.Read()) yield return reader.Current.AsDocument;
        }
    }
}
#endif
