#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFilePhase1_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-phase1-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedOneFilePhase1_Tests() => Directory.CreateDirectory(_directory);

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public async Task Retry_releases_obsolete_writer_holds_and_preserves_peer_callback_isolation(bool encrypted, bool pinned)
        {
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = true, Password = encrypted ? "secret" : null };
            using (var seed = new LiteDatabase(new ConnectionString { Filename = Filename, Password = settings.Password }))
            {
                seed.GetCollection("docs").Insert(Enumerable.Range(1, 150).Select(Row));
                seed.GetCollection("docs").EnsureIndex("value");
                seed.GetCollection("sentinel").Insert(Row(42));
            }
            using (var engine = new SharedEngine(settings))
            using (var peerEngine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            using (var peer = new LiteDatabase(peerEngine, disposeOnClose: false))
            {
                engine.MappedReaderTable.Should().BeSameAs(peerEngine.MappedReaderTable);
                engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
                using var anchor = pinned ? engine.Query("docs", new Query()) : null;
                if (pinned)
                {
                    anchor.Read().Should().BeTrue();
                    engine.Update("docs", new[] { Row(1) }).Should().Be(1);
                    Field<object>(engine, "_pin").Should().NotBeNull();
                }
                db.BeginTrans().Should().BeTrue();
                for (var retry = 0; retry < 3; retry++)
                {
                    db.BeginTrans().Should().BeFalse();
                    db.GetCollection("docs").Insert(Row(1000 + retry));
                    Action duplicate = () => db.GetCollection("docs").Insert(Row(1));
                    duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(110);
                    TransactionMonitor monitor = null;
                    var retained = Field<LiteEngine>(engine, "_engine");
                    if (retained != null)
                    {
                        monitor = Field<TransactionMonitor>(retained, "_monitor");
                        monitor.BeforeTransactionRegistration = () => throw new IOException("retry begin failure");
                    }
                    // Terminal abort may close the old core immediately. Install the
                    // registration failure on the independently reopened retry core.
                    engine.BeforeCountingUser = () =>
                    {
                        monitor = Field<TransactionMonitor>(Field<LiteEngine>(engine, "_engine"), "_monitor");
                        monitor.BeforeTransactionRegistration = () => throw new IOException("retry begin failure");
                    };
                    try
                    {
                        Action failedBegin = () => db.BeginTrans();
                        failedBegin.Should().Throw<IOException>().WithMessage("retry begin failure");
                    }
                    finally
                    {
                        engine.BeforeCountingUser = null;
                        if (monitor != null) monitor.BeforeTransactionRegistration = null;
                    }
                    db.BeginTrans().Should().BeTrue();
                    var map = engine.MappedReaderTable.Authority.Map;
                    (map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(0);
                    map.Load(SharedMemoryLayout.RecoveryOwner).Should().Be(0);
                    map.Load(SharedMemoryLayout.WriterOwner).Should().NotBe(0);
                    var holds = pinned ? Field<int>(Field<object>(engine, "_pin"), "_holds") : Field<int>(engine.MutexOwner, "_recursion");
                    holds.Should().Be(1, "retry must return the aborted transaction's hold exactly once");
                }
                var callbacks = 0;
                IEnumerable<BsonDocument> Input()
                {
                    callbacks++;
                    db.BeginTrans().Should().BeFalse();
                    Action callback = () => peer.BeginTrans();
                    callback.Should().Throw<InvalidOperationException>().WithMessage("*another connection*");
                    yield return Row(2000);
                }
                db.GetCollection("docs").Insert(Input()).Should().Be(1);
                callbacks.Should().Be(1);
                db.Commit().Should().BeTrue();
                if (!pinned) SharedOneFilePublicationFixture.Quiet(engine.MappedReaderTable);
                else engine.MappedReaderTable.Authority.Map.Load(SharedMemoryLayout.RecoveryOwner).Should().Be(0);
                await Task.Run(() => peer.GetCollection("docs").Insert(Row(2001))).WaitAsync(TimeSpan.FromSeconds(5));
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = Filename, Password = settings.Password });
                var rows = cold.GetCollection("docs");
                rows.FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 150).Concat(new[] { 2000, 2001 }).Select(Row));
                rows.Find(Query.EQ("value", 2000)).Should().BeEquivalentTo(new[] { Row(2000) });
                new[] { cold.GetCollection("sentinel").FindById(42) }.Should().BeEquivalentTo(new[] { Row(42) });
            }
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Protected_reopens_use_timeout_restored_from_the_WAL(bool encrypted)
        {
            var password = encrypted ? "secret" : null;
            using (var seed = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password }))
            {
                seed.GetCollection("docs").Insert(Row(1));
                seed.GetCollection("docs").EnsureIndex("value");
                seed.Checkpoint();
                seed.CheckpointSize = 0;
                seed.Timeout = TimeSpan.FromSeconds(1);
            }
            new FileInfo(FileHelper.GetLogFile(Filename)).Length.Should().BeGreaterThan(0);
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password, SharedOneFile = true });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.Timeout.Should().Be(TimeSpan.FromSeconds(1));
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Insert(Row(2));
            var locker = Field<LockService>(Field<LiteEngine>(engine, "_engine"), "_locker");
            var elapsed = Stopwatch.StartNew();
            Exception failure = null;
            var worker = new Thread(() => failure = Record.Exception(() =>
            {
                if (locker.EnterExclusive()) locker.ExitExclusive();
            })) { IsBackground = true };
            worker.Start();
            try
            {
                worker.Join(TimeSpan.FromSeconds(4)).Should().BeTrue("the WAL-restored timeout must expire while its owner remains alive");
                failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
                elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            }
            finally
            {
                db.Rollback().Should().BeTrue();
                SharedOneFilePublicationFixture.Quiet(engine.MappedReaderTable);
                worker.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                engine.Dispose();
            }
            using var cold = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password });
            cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(new[] { Row(1) });
            cold.GetCollection("docs").Find(Query.EQ("value", 1)).Should().BeEquivalentTo(new[] { Row(1) });
        }

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id, ["payload"] = new string('x', 128) };
        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
#endif
