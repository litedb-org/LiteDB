#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class GroupCommitReviewVisibility_Tests
    {
        [Theory]
        [InlineData("plain", true)]
        [InlineData("encrypted", false)]
        [InlineData("caller-file", false)]
        [InlineData("shared", false)]
        [InlineData("cache-only", false)]
        public void Grouping_is_restricted_to_durable_plain_direct_files(string mode, bool grouped)
        {
            using var file = new TempFile();
            using var logFile = new TempFile();
            using var data = mode == "caller-file" ? File.Open(file.Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
            using var log = mode == "caller-file" ? File.Open(logFile.Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
            var settings = new EngineSettings
            {
                Filename = file.Filename, Password = mode == "encrypted" ? "secret" : null,
                DurableCommits = mode != "cache-only", DataStream = data, LogStream = log
            };
            using ILiteEngine engine = mode == "shared" ? new SharedEngine(settings) : new LiteEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans();
            var direct = engine as LiteEngine ?? (LiteEngine)typeof(SharedEngine)
                .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            var disk = (DiskService)typeof(LiteEngine).GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(direct);
            (disk.CommitGroup != null).Should().Be(grouped);
            db.Rollback();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Dependent_writes_include_filestorage_and_system_indexes_obey_the_synced_prefix(bool failSecond)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, TransactionPageLimit = 1 });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            var refs = db.GetCollection("refs");
            rows.EnsureIndex("value");
            rows.Insert(new BsonDocument
            {
                ["_id"] = 1, ["value"] = 0,
                ["reference"] = new BsonDocument { ["$ref"] = "refs", ["$id"] = 1 }
            });
            refs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
            using (var content = new MemoryStream(Bytes(0))) db.FileStorage.Upload("blob", "blob.bin", content);
            db.Checkpoint();
            using var firstGate = new ManualResetEventSlim();
            using var nextGate = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseNext = new ManualResetEventSlim();
            using var appended = new CountdownEvent(2);
            var barriers = 0;
            var acknowledged = 0;
            byte[] syncedData = null, syncedLog = null;
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Signal();
                if (phase != "wal-before-durable-flush") return;
                var number = Interlocked.Increment(ref barriers);
                if (number == 1)
                {
                    firstGate.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                }
                else
                {
                    syncedData = TempFile.ReadAllBytesShared(file.Filename);
                    syncedLog = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
                    nextGate.Set();
                    releaseNext.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                }
            };
            NativeFileSync.SimulateErrno = path =>
                path == FileHelper.GetLogFile(file.Filename) && failSecond && Volatile.Read(ref barriers) == 2 ? 5 : 0;

            Task<Exception> Start(int value) => Task.Factory.StartNew(() =>
            {
                try
                {
                    db.BeginTrans();
                    var row = rows.FindById(1);
                    // The read starts on the durable prefix. The following write
                    // replaces its snapshot and can use A's pending pages.
                    row["value"] = value;
                    rows.Update(row);
                    refs.Update(new BsonDocument { ["_id"] = 1, ["value"] = value });
                    using (var content = new MemoryStream(Bytes(value))) db.FileStorage.Upload("blob", "blob.bin", content);
                    db.Commit();
                    Interlocked.Increment(ref acknowledged);
                    return null;
                }
                catch (Exception error) { return error; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var first = Start(1);
            Task<Exception> second = null;
            try
            {
                firstGate.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                second = Start(2);
                appended.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                acknowledged.Should().Be(0);
                Verify(db, 0);
                // Safepoints plus pending confirmations must keep the generation
                // and every pending version alive during partial checkpoint.
                engine.Checkpoint();
                Verify(db, 0);
                releaseFirst.Set();
                nextGate.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                await Completes(first);
                (await first).Should().BeNull();
                acknowledged.Should().Be(1);
                Verify(db, 1);
                releaseNext.Set();
                await Completes(second);
                if (failSecond)
                {
                    (await second).Should().BeAssignableTo<IOException>();
                    acknowledged.Should().Be(1);
                }
                else
                {
                    (await second).Should().BeNull();
                    acknowledged.Should().Be(2);
                    Verify(db, 2);
                }
            }
            finally
            {
                releaseFirst.Set();
                releaseNext.Set();
                try { await Completes(Task.WhenAll(new[] { first, second }.Where(task => task != null))); }
                finally
                {
                    NativeFileSync.SimulateErrno = null;
                    EngineState.SimulateProcessCrash = null;
                }
            }
            // Captured after A's successful sync. Checkpoint may already have
            // synced B too; all returned state must belong to a complete prefix.
            using var recoveredFile = new TempFile();
            File.WriteAllBytes(recoveredFile.Filename, syncedData);
            File.WriteAllBytes(FileHelper.GetLogFile(recoveredFile.Filename), syncedLog);
            for (var open = 0; open < 2; open++)
            {
                using var recovered = new LiteDatabase(recoveredFile.Filename);
                var value = recovered.GetCollection("rows").FindById(1)["value"].AsInt32;
                value.Should().BeInRange(1, 2);
                Verify(recovered, value);
                recovered.Checkpoint();
            }
        }

        private static byte[] Bytes(int value) => Enumerable.Repeat((byte)value, 18000).ToArray();

        private static void Verify(LiteDatabase db, int value)
        {
            var row = db.GetCollection("rows").Include("$.reference").FindById(1);
            row["value"].AsInt32.Should().Be(value);
            row["reference"]["value"].AsInt32.Should().Be(value);
            db.GetCollection("rows").Find(Query.EQ("value", value)).Should().ContainSingle();
            db.GetCollection("rows").Find(Query.EQ("value", (value + 1) % 3)).Should().BeEmpty();
            db.GetCollection("$indexes").FindAll().Should().Contain(index => index["collection"] == "rows" && index["name"] == "value");
            using var content = new MemoryStream();
            db.FileStorage.Download("blob", content);
            content.ToArray().Should().Equal(Bytes(value));
        }

        private static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(task);
            await task;
        }
    }
}
#endif
