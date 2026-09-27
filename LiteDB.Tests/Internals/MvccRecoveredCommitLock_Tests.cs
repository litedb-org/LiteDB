using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccRecoveredCommitLock_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var compact in new[] { false, true })
            foreach (var createCollection in new[] { false, true })
            foreach (var initialState in new[] { "recovered", "checkpointed", "legacy" })
                yield return new object[] { password, compact, createCollection, initialState };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void RecoveredHeaderKeepsCheckpointOutsideCommitPublication(string password, bool compact, bool createCollection, string initialState)
        {
            using var file = new TempFile();
            var settings = new EngineSettings
            {
                Filename = file.Filename,
                Password = password,
                CompactStorage = compact && initialState != "legacy" ? CompactStorageMode.Auto : CompactStorageMode.Legacy
            };
            using (var setup = new LiteEngine(settings))
            {
                setup.Pragma(Pragmas.CHECKPOINT, 0);
                setup.Insert("docs", Documents(0), BsonAutoId.Int32);
                setup.EnsureIndex("docs", "value", "$.value", false);
                setup.Insert("unrelated", Documents(7), BsonAutoId.Int32);
                if (initialState != "recovered") setup.Checkpoint();
            }

            if (initialState == "legacy")
            {
                IndexMigration_Tests.RewriteHeaders(file.Filename, password, header =>
                {
                    header[HeaderPage.P_FILE_VERSION] = 9;
                    header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                    Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
                });
            }
            settings.CompactStorage = compact ? CompactStorageMode.Auto : CompactStorageMode.Legacy;

            using (var engine = new LiteEngine(settings))
            {
                using var durable = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                using var checkpointAtLock = new ManualResetEventSlim();
                using var checkpointDone = new ManualResetEventSlim();
                Exception writerError = null;
                Exception checkpointError = null;
                var checkpointStarted = false;
                EngineState.SimulateProcessCrash = phase =>
                {
                    if (phase != "wal-before-index-confirmation") return;
                    durable.Set();
                    resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                };
                engine.CheckpointStage = stage =>
                {
                    if (stage == "before-commit-lock") checkpointAtLock.Set();
                };
                var writer = new Thread(() =>
                {
                    try
                    {
                        if (createCollection) engine.Insert("created", Documents(1), BsonAutoId.Int32);
                        else engine.Update("docs", Documents(1));
                    }
                    catch (Exception ex) { writerError = ex; }
                }) { IsBackground = true };
                var checkpoint = new Thread(() =>
                {
                    try { engine.Checkpoint(); }
                    catch (Exception ex) { checkpointError = ex; }
                    finally { checkpointDone.Set(); }
                }) { IsBackground = true };
                writer.Start();
                try
                {
                    durable.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    checkpoint.Start();
                    checkpointStarted = true;
                    checkpointAtLock.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    checkpointDone.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse(
                        "the durable commit has not published its WAL index yet");
                }
                finally
                {
                    resume.Set();
                    var writerJoined = writer.Join(TimeSpan.FromSeconds(10));
                    var checkpointJoined = !checkpointStarted || checkpoint.Join(TimeSpan.FromSeconds(10));
                    EngineState.SimulateProcessCrash = null;
                    engine.CheckpointStage = null;
                    writerJoined.Should().BeTrue();
                    checkpointJoined.Should().BeTrue();
                }
                if (writerError != null) ExceptionDispatchInfo.Capture(writerError).Throw();
                if (checkpointError != null) ExceptionDispatchInfo.Capture(checkpointError).Throw();
                engine.Checkpoint();
            }

            using var reopened = new LiteDatabase(new LiteEngine(settings));
            reopened.GetCollection("docs").FindAll().Should().BeEquivalentTo(Documents(createCollection ? 0 : 1));
            reopened.GetCollection("docs").Find(Query.EQ("value", createCollection ? 0 : 1))
                .Should().BeEquivalentTo(Documents(createCollection ? 0 : 1));
            reopened.GetCollection("unrelated").FindAll().Should().BeEquivalentTo(Documents(7));
            if (createCollection) reopened.GetCollection("created").FindAll().Should().BeEquivalentTo(Documents(1));
        }

        private static BsonDocument[] Documents(int value) => Enumerable.Range(1, 16)
            .Select(id => new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 500) }).ToArray();
    }
}
