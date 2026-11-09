#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureReacquisitionFault_Tests
    {
        [OneFileTheory]
        [InlineData("short-read", null, false)] [InlineData("read", null, false)] [InlineData("sync", null, false)]
        [InlineData("short-read", "secret", true)] [InlineData("read", "secret", true)] [InlineData("sync", "secret", true)]
        public void Reacquisition_IO_failure_releases_ownership_and_retry_preserves_remote_commits_and_old_snapshots(string fault, string password, bool legacy)
        {
            using var files = new SharedOneFilePublicationFixture(password, legacy);
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.Update("docs", new[] { Row(1, 0) });
                SharedOneFileWarmReads_Tests.Warm(engine, db);
                using var old = engine.Query("docs", new Query());
                old.Read().Should().BeTrue();
                using (var remote = new MvccProcess("one-file-warm-writer", files.Filename, password))
                {
                    remote.Expect("ready").GetAwaiter().GetResult();
                    remote.Send("commit"); remote.Expect("committed").GetAwaiter().GetResult();
                    remote.Send("exit"); remote.Finish().GetAwaiter().GetResult();
                }
                var hits = 0;
                SharedOwnershipEvents.CoreStage = (facade, core, stage) =>
                {
                    if (!ReferenceEquals(facade, engine) || stage != SharedOwnershipEvents.Opened || fault == "sync") return;
                    State(core).SimulateDiskReadFail = _ =>
                    {
                        hits++;
                        if (fault == "short-read") throw new EndOfStreamException("injected incomplete reacquired page");
                        throw new IOException("injected reacquired page read failure");
                    };
                };
                NativeFileSync.SimulateErrno = path =>
                {
                    if (fault != "sync" || path != files.Log) return 0;
                    hits++;
                    return 5;
                };
                try
                {
                    ((Action)(() => engine.Update("docs", new[] { Row(1, 99) }))).Should().Throw<IOException>();
                    hits.Should().BeGreaterThan(0, "the injected page-read or real file-sync path must be reached");
                }
                finally { SharedOwnershipEvents.CoreStage = null; NativeFileSync.SimulateErrno = null; }
                var failedPin = engine.Pin;
                if (failedPin != null) { failedPin.RequestRelease(true); failedPin.WaitReleased(); }
                files.WriteIsFree();
                for (var retry = 0; retry < 2; retry++)
                {
                    engine.Update("docs", new[] { Row(2, 2) }).Should().Be(1);
                    db.GetCollection("docs").Find(Query.EQ("value", 1)).Should().BeEquivalentTo(new[] { Row(1, 1) });
                    db.GetCollection("docs").Find(Query.EQ("value", 99)).Should().BeEmpty();
                }
                new[] { old.Current.AsDocument }.Concat(Rows(old)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(new LiteDB.Engine.LiteEngine(files.Settings));
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? 1 : id == 2 ? 2 : 0)));
                cold.GetCollection("docs").Find(Query.EQ("value", 2)).Should().BeEquivalentTo(new[] { Row(2, 2) });
                cold.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
            }
        }

        private static IEnumerable<BsonDocument> Rows(IBsonDataReader reader)
        {
            while (reader.Read()) yield return reader.Current.AsDocument;
        }
    }
}
#endif
