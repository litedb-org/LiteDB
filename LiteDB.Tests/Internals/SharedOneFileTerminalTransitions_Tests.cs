#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileTerminalTransitions_Tests
    {
        [OneFileTheory]
        [InlineData(false, "duplicate")] [InlineData(true, "duplicate")]
        [InlineData(false, "stop")] [InlineData(true, "stop")]
        [InlineData(false, "commit")] [InlineData(true, "commit")]
        [InlineData(false, "rollback")] [InlineData(true, "rollback")]
        [InlineData(false, "commit-failure")] [InlineData(true, "commit-failure")]
        [InlineData(false, "rollback-failure")] [InlineData(true, "rollback-failure")]
        [InlineData(false, "close-failure")] [InlineData(true, "close-failure")]
        [InlineData(false, "dispose-failure")] [InlineData(true, "dispose-failure")]
        public void Every_terminal_transition_releases_to_a_peer_and_preserves_cold_indexed_state(bool pinned, string terminal)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var peerEngine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var peer = new LiteDatabase(peerEngine, disposeOnClose: false);
            engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
            using var anchor = pinned ? engine.Query("docs", new Query()) : null;
            if (pinned) { anchor.Read().Should().BeTrue(); engine.Update("docs", new[] { Row(1, 0) }); }
            var pin = engine.Pin;
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1, 1));
            var core = Core(engine);
            var hits = 0;
            engine.OwnershipStage = stage =>
            {
                if (((terminal == "close-failure" || terminal == "dispose-failure") && stage == "core-closed") ||
                    (terminal == "rollback-failure" && stage == "before-rollback"))
                {
                    Interlocked.Increment(ref hits);
                    throw new IOException("terminal ownership cut");
                }
            };
            State(core).AtCrashPoint = stage =>
            {
                if (terminal == "commit-failure" && stage == "wal-before-durable-flush")
                { hits++; throw new IOException("terminal commit cut"); }
            };
            Exception failure;
            try
            {
                failure = Record.Exception(() =>
                {
                    switch (terminal)
                    {
                        case "duplicate": db.GetCollection("docs").Insert(Row(2, 99)); break;
                        case "stop":
                            IEnumerable<BsonDocument> StoppedInput()
                            {
                                State(core).Stop(new IOException("terminal Stop"));
                                throw new IOException("stopped input");
#pragma warning disable CS0162
                                yield break;
#pragma warning restore CS0162
                            }
                            db.GetCollection("docs").Insert(StoppedInput());
                            break;
                        case "commit": case "commit-failure": db.Commit(); break;
                        case "dispose-failure": engine.Dispose(); break;
                        default: db.Rollback(); break;
                    }
                    if (pin != null) { pin.RequestRelease(force: false); pin.WaitReleased(); }
                });
                if (terminal == "commit" || terminal == "rollback") failure.Should().BeNull();
                else failure.Should().NotBeNull();
                if (terminal.EndsWith("failure")) hits.Should().BeGreaterThan(0);
            }
            finally { engine.OwnershipStage = null; State(core).AtCrashPoint = null; }
            if (pin != null)
            {
                pin.RequestRelease(force: true);
                try { pin.WaitReleased(); } catch (IOException) { }
            }
            files.WriteIsFree();
            // No retry, rollback or Dispose through the original facade before peer admission.
            peer.BeginTrans().Should().BeTrue();
            peer.GetCollection("docs").Update(Row(2, 2));
            peer.Commit().Should().BeTrue();
            var expected = terminal == "commit" ? 1 : terminal == "commit-failure"
                ? peer.GetCollection("docs").FindById(1)["value"].AsInt32 : 0;
            expected.Should().BeOneOf(0, 1);
            engine.Dispose();
            peerEngine.Dispose();
            anchor?.Dispose();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(files.Filename);
                cold.GetCollection("docs").FindById(2).As<object>().Should().BeEquivalentTo(Row(2, 2));
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(
                    System.Linq.Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? expected : id == 2 ? 2 : 0)));
                cold.GetCollection("docs").Find(Query.EQ("value", 2)).Should().BeEquivalentTo(new[] { Row(2, 2) });
                cold.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
            }
        }

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Deferred_disposal_from_input_or_transform_keeps_the_last_participant_until_WRITE_release(bool sibling, bool transform)
        {
            using var files = new SharedOneFilePublicationFixture();
            SharedEngine engine = null;
            var armed = false;
            var calls = 0;
            var settings = files.Settings;
            if (transform) settings.ReadTransform = (_, value) =>
            {
                if (armed) { armed = false; calls++; engine.Dispose(); }
                return value;
            };
            using (engine = new SharedEngine(settings))
            using (var peer = sibling ? new SharedEngine(settings) : null)
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
                using var anchor = engine.Query("docs", new Query());
                anchor.Read().Should().BeTrue();
                engine.Update("docs", new[] { Row(1, 0) });
                var pin = engine.Pin;
                pin.Should().NotBeNull();
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("docs").Update(Row(1, 1));
                var table = engine.MappedReaderTable;
                if (transform)
                {
                    armed = true;
                    using var result = engine.Query("docs", new Query());
                    Action afterDispose = () => result.Read();
                    afterDispose.Should().Throw<ObjectDisposedException>();
                }
                else
                {
                    IEnumerable<BsonDocument> Input()
                    { calls++; engine.Dispose(); yield return Row(1000, 1); }
                    db.GetCollection("docs").Insert(Input()).Should().Be(1);
                }
                pin.WaitReleased();
                calls.Should().Be(1);
                files.WriteIsFree();
                anchor.Dispose();
                if (sibling)
                {
                    using var peerDb = new LiteDatabase(peer, disposeOnClose: false);
                    peerDb.BeginTrans().Should().BeTrue();
                    peerDb.Rollback().Should().BeTrue();
                }
                else table.Closed.Should().BeTrue();
            }
            for (var repeat = 0; repeat < 2; repeat++) { using var cold = new LiteDatabase(files.Filename); Verify(cold, 0); }
        }
    }
}
#endif
