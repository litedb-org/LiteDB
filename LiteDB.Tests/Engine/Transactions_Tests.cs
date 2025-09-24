﻿using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;
using Xunit.Sdk;

namespace LiteDB.Tests.Engine
{
    using System;

    public class Transactions_Tests
    {
        const int MIN_CPU_COUNT = 2;
        
        [CpuBoundFact(MIN_CPU_COUNT)]
        public async Task Transaction_Write_Lock_Timeout()
        {
            var data1 = DataGen.Person(1, 100).ToArray();
            var data2 = DataGen.Person(101, 200).ToArray();

            using (var db = DatabaseFactory.Create(connectionString: "filename=:memory:"))
            {
                // configure the minimal pragma timeout and then override the engine to a few milliseconds
                db.Pragma(Pragmas.TIMEOUT, 1);
                SetEngineTimeout(db, TimeSpan.FromMilliseconds(20));

                var person = db.GetCollection<Person>();

                // init person collection with 100 document
                person.Insert(data1);

                var taskASemaphore = new SemaphoreSlim(0, 1);
                var taskBSemaphore = new SemaphoreSlim(0, 1);

                // task A will open transaction and will insert +100 documents
                // but will commit only after task B observes the timeout
                var ta = Task.Run(() =>
                {
                    db.BeginTrans();

                    person.Insert(data2);

                    taskBSemaphore.Release();
                    taskASemaphore.Wait();

                    var count = person.Count();

                    count.Should().Be(data1.Length + data2.Length);

                    db.Commit();
                });

                // task B will try delete all documents but will be locked until the short timeout is hit
                var tb = Task.Run(() =>
                {
                    taskBSemaphore.Wait();

                    db.BeginTrans();
                    person
                        .Invoking(personCol => personCol.DeleteMany("1 = 1"))
                        .Should()
                        .Throw<LiteException>()
                        .Where(ex => ex.ErrorCode == LiteException.LOCK_TIMEOUT);

                    taskASemaphore.Release();
                });

                await Task.WhenAll(ta, tb);
            }
        }

        
        [CpuBoundFact(MIN_CPU_COUNT)]
        public async Task Transaction_Avoid_Dirty_Read()
        {
            var data1 = DataGen.Person(1, 100).ToArray();
            var data2 = DataGen.Person(101, 200).ToArray();

            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var person = db.GetCollection<Person>();

                // init person collection with 100 document
                person.Insert(data1);

                var taskASemaphore = new SemaphoreSlim(0, 1);
                var taskBSemaphore = new SemaphoreSlim(0, 1);

                // task A will open transaction and will insert +100 documents 
                // but will commit only 1s later - this plus +100 document must be visible only inside task A
                var ta = Task.Run(() =>
                {
                    db.BeginTrans();

                    person.Insert(data2);

                    taskBSemaphore.Release();
                    taskASemaphore.Wait();

                    var count = person.Count();

                    count.Should().Be(data1.Length + data2.Length);

                    db.Commit();
                    taskBSemaphore.Release();
                });

                // task B will not open transaction and will wait 250ms before and count collection - 
                // at this time, task A already insert +100 document but here I can't see (are not committed yet)
                // after task A finish, I can see now all 200 documents
                var tb = Task.Run(() =>
                {
                    taskBSemaphore.Wait();

                    var count = person.Count();

                    // read 100 documents
                    count.Should().Be(data1.Length);

                    taskASemaphore.Release();
                    taskBSemaphore.Wait();

                    // read 200 documents
                    count = person.Count();

                    count.Should().Be(data1.Length + data2.Length);
                });

                await Task.WhenAll(ta, tb);
            }
        }
       

        [CpuBoundFact(MIN_CPU_COUNT)]
        public async Task Transaction_Read_Version()
        {
            var data1 = DataGen.Person(1, 100).ToArray();
            var data2 = DataGen.Person(101, 200).ToArray();

            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var person = db.GetCollection<Person>();

                // init person collection with 100 document
                person.Insert(data1);

                var taskASemaphore = new SemaphoreSlim(0, 1);
                var taskBSemaphore = new SemaphoreSlim(0, 1);

                // task A will insert more 100 documents but will commit only 1s later
                var ta = Task.Run(() =>
                {
                    db.BeginTrans();

                    person.Insert(data2);

                    taskBSemaphore.Release();
                    taskASemaphore.Wait();

                    db.Commit();

                    taskBSemaphore.Release();
                });

                // task B will open transaction too and will count 100 original documents only
                // but now, will wait task A finish - but is in transaction and must see only initial version
                var tb = Task.Run(() =>
                {
                    db.BeginTrans();

                    taskBSemaphore.Wait();

                    var count = person.Count();

                    // read 100 documents
                    count.Should().Be(data1.Length);

                    taskASemaphore.Release();
                    taskBSemaphore.Wait();

                    // keep reading 100 documents because i'm still in same transaction
                    count = person.Count();

                    count.Should().Be(data1.Length);
                });

                await Task.WhenAll(ta, tb);
            }
        }

        [CpuBoundFact(MIN_CPU_COUNT)]
        public void Test_Transaction_States()
        {
            var data0 = DataGen.Person(1, 10).ToArray();
            var data1 = DataGen.Person(11, 20).ToArray();

            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var person = db.GetCollection<Person>();

                // first time transaction will be opened
                db.BeginTrans().Should().BeTrue();

                // but in second type transaction will be same
                db.BeginTrans().Should().BeFalse();

                person.Insert(data0);

                // must commit transaction
                db.Commit().Should().BeTrue();

                // no transaction to commit
                db.Commit().Should().BeFalse();

                // no transaction to rollback;
                db.Rollback().Should().BeFalse();

                db.BeginTrans().Should().BeTrue();

                // no page was changed but ok, let's rollback anyway
                db.Rollback().Should().BeTrue();

                // auto-commit
                person.Insert(data1);

                person.Count().Should().Be(20);
            }
        }

#if DEBUG || TESTING
        [Fact]
        public void Transaction_Rollback_Should_Skip_ReadOnly_Buffers_From_Safepoint()
        {
            using var db = DatabaseFactory.Create();
            var collection = db.GetCollection<BsonDocument>("docs");

            db.BeginTrans().Should().BeTrue();

            for (var i = 0; i < 10; i++)
            {
                collection.Insert(new BsonDocument
                {
                    ["_id"] = i,
                    ["value"] = $"value-{i}"
                });
            }

            var engine = GetLiteEngine(db);
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetThreadTransaction();

            transaction.Should().NotBeNull();

            var transactionService = transaction!;
            transactionService.Pages.TransactionSize.Should().BeGreaterThan(0);

            transactionService.MaxTransactionSize = Math.Max(1, transactionService.Pages.TransactionSize);
            SetMonitorFreePages(monitor, 0);

            transactionService.Safepoint();
            transactionService.Pages.TransactionSize.Should().Be(0);

            var snapshot = transactionService.Snapshots.Single();
            snapshot.CollectionPage.Should().NotBeNull();

            var collectionPage = snapshot.CollectionPage!;
            collectionPage.IsDirty = true;

            var buffer = collectionPage.Buffer;

            try
            {
                buffer.ShareCounter = 1;

                var shareCounters = snapshot
                    .GetWritablePages(true, true)
                    .Select(page => page.Buffer.ShareCounter)
                    .ToList();

                shareCounters.Should().NotBeEmpty();
                shareCounters.Should().Contain(counter => counter != Constants.BUFFER_WRITABLE);

                db.Rollback().Should().BeTrue();
            }
            finally
            {
                buffer.ShareCounter = 0;
            }

            collection.Count().Should().Be(0);
        }

        [Fact]
        public void Transaction_Rollback_Should_Discard_Writable_Dirty_Pages()
        {
            using var db = DatabaseFactory.Create();
            var collection = db.GetCollection<BsonDocument>("docs");

            db.BeginTrans().Should().BeTrue();

            for (var i = 0; i < 3; i++)
            {
                collection.Insert(new BsonDocument
                {
                    ["_id"] = i,
                    ["value"] = $"value-{i}"
                });
            }

            var engine = GetLiteEngine(db);
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetThreadTransaction();

            transaction.Should().NotBeNull();

            var transactionService = transaction!;
            var snapshot = transactionService.Snapshots.Single();

            var shareCounters = snapshot
                .GetWritablePages(true, true)
                .Select(page => page.Buffer.ShareCounter)
                .ToList();

            shareCounters.Should().NotBeEmpty();
            shareCounters.Should().OnlyContain(counter => counter == Constants.BUFFER_WRITABLE);

            db.Rollback().Should().BeTrue();

            collection.Count().Should().Be(0);
        }

#endif

        private class BlockingStream : MemoryStream
        {
            public readonly AutoResetEvent   Blocked       = new AutoResetEvent(false);
            public readonly ManualResetEvent ShouldUnblock = new ManualResetEvent(false);
            public          bool             ShouldBlock;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (this.ShouldBlock)
                {
                    this.Blocked.Set();
                    this.ShouldUnblock.WaitOne();
                    this.Blocked.Reset();
                }
                base.Write(buffer, offset, count);
            }
        }

        [CpuBoundFact(MIN_CPU_COUNT)]
        public void Test_Transaction_ReleaseWhenFailToStart()
        {
            var    blockingStream             = new BlockingStream();
            var    db                         = new LiteDatabase(blockingStream);
            SetEngineTimeout(db, TimeSpan.FromMilliseconds(50));
            Thread lockerThread               = null;
            try
            {
                lockerThread = new Thread(() =>
                {
                    db.GetCollection<Person>().Insert(new Person());
                    blockingStream.ShouldBlock = true;
                    db.Checkpoint();
                    db.Dispose();
                })
                {
                    IsBackground = true
                };
                lockerThread.Start();
                blockingStream.Blocked.WaitOne(200).Should().BeTrue();
                Assert.Throws<LiteException>(() => db.GetCollection<Person>().Insert(new Person())).Message.Should().Contain("timeout");
                Assert.Throws<LiteException>(() => db.GetCollection<Person>().Insert(new Person())).Message.Should().Contain("timeout");
            }
            finally
            {
                blockingStream.ShouldUnblock.Set();
                lockerThread?.Join();
            }
        }

        private static LiteEngine GetLiteEngine(LiteDatabase database)
        {
            var engineField = typeof(LiteDatabase).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new InvalidOperationException("Unable to locate LiteDatabase engine field.");

            if (engineField.GetValue(database) is not LiteEngine engine)
            {
                throw new InvalidOperationException("LiteDatabase engine is not initialized.");
            }

            return engine;
        }

        private static void SetMonitorFreePages(TransactionMonitor monitor, int value)
        {
            var freePagesField = typeof(TransactionMonitor).GetField("_freePages", BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new InvalidOperationException("Unable to locate TransactionMonitor free pages field.");

            freePagesField.SetValue(monitor, value);
        }

        private static void SetEngineTimeout(LiteDatabase database, TimeSpan timeout)
        {
            var engine = GetLiteEngine(database);

            var headerField = typeof(LiteEngine).GetField("_header", BindingFlags.Instance | BindingFlags.NonPublic);
            var header      = headerField?.GetValue(engine) ?? throw new InvalidOperationException("LiteEngine header not available.");
            var pragmasProp = header.GetType().GetProperty("Pragmas", BindingFlags.Instance | BindingFlags.Public) ?? throw new InvalidOperationException("Engine pragmas not accessible.");
            var pragmas     = pragmasProp.GetValue(header) ?? throw new InvalidOperationException("Engine pragmas not available.");
            var timeoutProp = pragmas.GetType().GetProperty("Timeout", BindingFlags.Instance | BindingFlags.Public) ?? throw new InvalidOperationException("Timeout property not found.");
            var setter      = timeoutProp.GetSetMethod(true) ?? throw new InvalidOperationException("Timeout setter not accessible.");

            setter.Invoke(pragmas, new object[] { timeout });
        }
    }
}
