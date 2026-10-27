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
    public class GroupCommitDependency_Tests
    {
        [Theory]
        [InlineData("update-missing", false)]
        [InlineData("update-missing", true)]
        [InlineData("delete-many", false)]
        [InlineData("delete-many", true)]
        [InlineData("update-many", false)]
        [InlineData("update-many", true)]
        [InlineData("drop-index", false)]
        [InlineData("drop-index", true)]
        [InlineData("ensure-index", false)]
        [InlineData("ensure-index", true)]
        [InlineData("explicit-noop", false)]
        [InlineData("explicit-noop", true)]
        [InlineData("upsert-existing", false)]
        [InlineData("upsert-existing", true)]
        [InlineData("unique-error", false)]
        [InlineData("unique-error", true)]
        [InlineData("explicit-unique-error", false)]
        [InlineData("explicit-unique-error", true)]
        [InlineData("index-error", false)]
        [InlineData("index-error", true)]
        [InlineData("for-update-query", false)]
        [InlineData("for-update-query", true)]
        public async Task Results_and_errors_cover_the_pending_snapshot_dependency(string operation, bool failSync)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", unique: true);
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.Checkpoint();
            var data = TempFile.ReadAllBytesShared(file.Filename);
            var log = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
            byte[] syncedData = null, syncedLog = null;
            using var appended = new ManualResetEventSlim();
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Set();
            };
            var syncs = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != FileHelper.GetLogFile(file.Filename)) return 0;
                Interlocked.Increment(ref syncs);
                if (failSync) return 5;
                syncedData = TempFile.ReadAllBytesShared(file.Filename);
                syncedLog = TempFile.ReadAllBytesShared(FileHelper.GetLogFile(file.Filename));
                return 0;
            };
            Task<Exception> first = null;
            Exception resultError = null;
            var barriersAtResult = -1;
            try
            {
                lock (Sync(engine))
                {
                    first = Start(() =>
                    {
                        if (operation == "drop-index") rows.DropIndex("value").Should().BeTrue();
                        else if (operation.EndsWith("unique-error") || operation == "upsert-existing")
                            rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = 99 });
                        else if (operation == "index-error" || operation == "ensure-index")
                            rows.EnsureIndex("pending", "$.value").Should().BeTrue();
                        else rows.Delete(1).Should().BeTrue();
                    });
                    appended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    try
                    {
                        switch (operation)
                        {
                            case "update-missing":
                                rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 43 }).Should().BeFalse();
                                break;
                            case "delete-many": rows.DeleteMany("value = 42").Should().Be(0); break;
                            case "update-many": rows.UpdateMany("{ value: 43 }", "value = 42").Should().Be(0); break;
                            case "drop-index": rows.DropIndex("value").Should().BeFalse(); break;
                            case "ensure-index": rows.EnsureIndex("pending", "$.value").Should().BeFalse(); break;
                            case "explicit-noop":
                                db.BeginTrans().Should().BeTrue();
                                rows.Delete(1).Should().BeFalse();
                                db.Commit().Should().BeTrue();
                                break;
                            case "upsert-existing":
                                rows.Upsert(new BsonDocument { ["_id"] = 2, ["value"] = 100 }).Should().BeFalse();
                                break;
                            case "explicit-unique-error": db.BeginTrans().Should().BeTrue(); goto case "unique-error";
                            case "unique-error":
                                rows.Insert(new BsonDocument { ["_id"] = 3, ["value"] = 99 });
                                break;
                            case "index-error": rows.EnsureIndex("pending", "$.other"); break;
                            case "for-update-query":
                                using (var reader = engine.Query("rows", new Query { ForUpdate = true }))
                                    reader.Read().Should().BeFalse();
                                break;
                        }
                    }
                    catch (Exception error) { resultError = error; }
                    barriersAtResult = Volatile.Read(ref syncs);
                }
                await Completes(first);
            }
            finally
            {
                if (first != null) await Completes(first);
                NativeFileSync.SimulateErrno = null;
                EngineState.SimulateProcessCrash = null;
            }
            barriersAtResult.Should().Be(1, "a result or constraint error must cover the pending version it observed");
            if (failSync)
            {
                resultError.Should().BeAssignableTo<IOException>();
                (await first).Should().BeAssignableTo<IOException>();
                Action later = () => rows.Count();
                later.Should().Throw<IOException>();
            }
            else
            {
                (await first).Should().BeNull();
                if (operation.EndsWith("unique-error"))
                    resultError.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
                else if (operation == "index-error") resultError.Should().BeOfType<LiteException>();
                else resultError.Should().BeNull();
                syncedData.Should().NotBeNull();
                syncedLog.Should().NotBeNull();
            }

            // Lose all writes after the last successful barrier, then recover,
            // checkpoint and recover again. Check exact primary/secondary views.
            using var recoveredFile = new TempFile();
            File.WriteAllBytes(recoveredFile.Filename, failSync ? data : syncedData);
            File.WriteAllBytes(FileHelper.GetLogFile(recoveredFile.Filename), failSync ? log : syncedLog);
            for (var pass = 0; pass < 2; pass++)
            {
                using var recovered = new LiteDatabase(recoveredFile.Filename);
                var recoveredRows = recovered.GetCollection("rows");
                var inserted = operation.EndsWith("unique-error") || operation == "upsert-existing";
                var deleted = operation == "update-missing" || operation == "delete-many" ||
                    operation == "update-many" || operation == "for-update-query" || operation == "explicit-noop";
                recoveredRows.Count().Should().Be(failSync ? 1 : inserted ? 2 : deleted ? 0 : 1);
                if (failSync || !deleted)
                    recoveredRows.FindById(1)["value"].AsInt32.Should().Be(42);
                else Assert.Null(recoveredRows.FindById(1));
                var secondValue = operation == "upsert-existing" ? 100 : 99;
                recoveredRows.Find(Query.EQ("value", secondValue)).Select(row => row["_id"].AsInt32)
                    .Should().Equal(!failSync && inserted ? new[] { 2 } : Array.Empty<int>());
                var indexes = recovered.GetCollection("$indexes").FindAll()
                    .Where(index => index["collection"] == "rows").Select(index => index["name"].AsString).OrderBy(name => name);
                indexes.Should().Equal(!failSync && operation == "drop-index" ? new[] { "_id" } :
                    !failSync && (operation == "index-error" || operation == "ensure-index") ?
                    new[] { "_id", "pending", "value" } : new[] { "_id", "value" });
                recovered.Checkpoint();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Read_only_explicit_transactions_remain_on_the_durable_prefix(bool promoteToWrite)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.Checkpoint();
            using var appended = new ManualResetEventSlim();
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "wal-after-index-confirmation") appended.Set();
            };
            var syncs = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != FileHelper.GetLogFile(file.Filename)) return 0;
                Interlocked.Increment(ref syncs);
                return 5;
            };
            Task<Exception> first = null;
            try
            {
                lock (Sync(engine))
                {
                    first = Start(() => rows.Delete(1).Should().BeTrue());
                    appended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    db.BeginTrans().Should().BeTrue();
                    rows.FindById(1)["value"].AsInt32.Should().Be(42);
                    rows.Find("value = 42").Count().Should().Be(1);
                    Volatile.Read(ref syncs).Should().Be(0);
                    if (promoteToWrite)
                    {
                        rows.Delete(1).Should().BeFalse();
                        Action commit = () => db.Commit();
                        commit.Should().Throw<IOException>();
                        Volatile.Read(ref syncs).Should().Be(1);
                    }
                    else
                    {
                        db.Commit().Should().BeTrue();
                        Volatile.Read(ref syncs).Should().Be(0);
                    }
                }
                await Completes(first);
                (await first).Should().BeAssignableTo<IOException>();
            }
            finally
            {
                if (first != null) await Completes(first);
                NativeFileSync.SimulateErrno = null;
                EngineState.SimulateProcessCrash = null;
            }
        }

        private static object Sync(LiteEngine engine)
        {
            var disk = (DiskService)typeof(LiteEngine).GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            return typeof(WalCommitGroup).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(disk.CommitGroup);
        }

        private static Task<Exception> Start(Action action) => Task.Factory.StartNew(() =>
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)))).Should().BeSameAs(task);
            await task;
        }
    }
}
#endif
