using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    using System;

    public class Transactions_Tests
    {
        [Fact]
        public async Task Transaction_Write_Lock_Timeout()
        {
            var data1 = DataGen.Person(1, 100).ToArray();
            var data2 = DataGen.Person(101, 200).ToArray();

            using (var db = new LiteDatabase("filename=:memory:"))
            {
                // small timeout
                db.Pragma(Pragmas.TIMEOUT, 1);

                var person = db.GetCollection<Person>();

                // init person collection with 100 document
                person.Insert(data1);

                using var taskASemaphore = new SemaphoreSlim(0, 1);
                using var taskBSemaphore = new SemaphoreSlim(0, 1);
                var waitTimeout = TimeSpan.FromSeconds(5);

                // task A will open transaction and will insert +100 documents
                // but will commit only 2s later
                var ta = Task.Run(async () =>
                {
                    var startReleased = false;

                    try
                    {
                        db.BeginTrans();

                        person.Insert(data2);

                        taskBSemaphore.Release();
                        startReleased = true;

                        (await taskASemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task B should release task A after asserting the delete timeout");

                        var count = person.Count();

                        count.Should().Be(data1.Length + data2.Length);

                        db.Commit();
                    }
                    finally
                    {
                        if (!startReleased)
                        {
                            taskBSemaphore.Release();
                        }
                    }
                });

                // task B will try delete all documents but will be locked during 1 second
                var tb = Task.Run(async () =>
                {
                    try
                    {
                        (await taskBSemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task A should start inserting data before task B attempts the delete");

                        db.BeginTrans();
                        person
                            .Invoking(personCol => personCol.DeleteMany("1 = 1"))
                            .Should()
                            .Throw<LiteException>()
                            .Where(ex => ex.ErrorCode == LiteException.LOCK_TIMEOUT);
                    }
                    finally
                    {
                        taskASemaphore.Release();
                    }
                });

                await Task.WhenAll(ta, tb);
            }
        }


        [Fact]
        public async Task Transaction_Avoid_Dirty_Read()
        {
            var data1 = DataGen.Person(1, 100).ToArray();
            var data2 = DataGen.Person(101, 200).ToArray();

            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var person = db.GetCollection<Person>();

                // init person collection with 100 document
                person.Insert(data1);

                using var taskASemaphore = new SemaphoreSlim(0, 1);
                using var taskBSemaphore = new SemaphoreSlim(0, 2);
                var waitTimeout = TimeSpan.FromSeconds(5);

                // task A will open transaction and will insert +100 documents
                // but will commit only 1s later - this plus +100 document must be visible only inside task A
                var ta = Task.Run(async () =>
                {
                    var initialSignalSent = false;
                    var completionSignalSent = false;

                    try
                    {
                        db.BeginTrans();

                        person.Insert(data2);

                        taskBSemaphore.Release();
                        initialSignalSent = true;

                        (await taskASemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task B should confirm the original document count before task A commits");

                        var count = person.Count();

                        count.Should().Be(data1.Length + data2.Length);

                        db.Commit();

                        taskBSemaphore.Release();
                        completionSignalSent = true;
                    }
                    finally
                    {
                        if (!initialSignalSent)
                        {
                            taskBSemaphore.Release();
                        }

                        if (!completionSignalSent)
                        {
                            taskBSemaphore.Release();
                        }
                    }
                });

                // task B will not open transaction and will wait 250ms before and count collection -
                // at this time, task A already insert +100 document but here I can't see (are not committed yet)
                // after task A finish, I can see now all 200 documents
                var tb = Task.Run(async () =>
                {
                    var releasedTaskA = false;

                    try
                    {
                        (await taskBSemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task A should signal before task B reads the uncommitted data");

                        var count = person.Count();

                        // read 100 documents
                        count.Should().Be(data1.Length);

                        taskASemaphore.Release();
                        releasedTaskA = true;

                        (await taskBSemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task A should signal completion after committing the transaction");

                        // read 200 documents
                        count = person.Count();

                        count.Should().Be(data1.Length + data2.Length);
                    }
                    finally
                    {
                        if (!releasedTaskA)
                        {
                            taskASemaphore.Release();
                        }
                    }
                });

                await Task.WhenAll(ta, tb);
            }
        }

        [Fact]
        public async Task Transaction_Read_Version()
        {
            var data1 = DataGen.Person(1, 100).ToArray();
            var data2 = DataGen.Person(101, 200).ToArray();

            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var person = db.GetCollection<Person>();

                // init person collection with 100 document
                person.Insert(data1);

                using var taskASemaphore = new SemaphoreSlim(0, 1);
                using var taskBSemaphore = new SemaphoreSlim(0, 2);
                var waitTimeout = TimeSpan.FromSeconds(5);

                // task A will insert more 100 documents but will commit only 1s later
                var ta = Task.Run(async () =>
                {
                    var initialSignalSent = false;
                    var completionSignalSent = false;

                    try
                    {
                        db.BeginTrans();

                        person.Insert(data2);

                        taskBSemaphore.Release();
                        initialSignalSent = true;

                        (await taskASemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task B should start reading before task A commits");

                        db.Commit();

                        taskBSemaphore.Release();
                        completionSignalSent = true;
                    }
                    finally
                    {
                        if (!initialSignalSent)
                        {
                            taskBSemaphore.Release();
                        }

                        if (!completionSignalSent)
                        {
                            taskBSemaphore.Release();
                        }
                    }
                });

                // task B will open transaction too and will count 100 original documents only
                // but now, will wait task A finish - but is in transaction and must see only initial version
                var tb = Task.Run(async () =>
                {
                    var releasedTaskA = false;

                    try
                    {
                        db.BeginTrans();

                        (await taskBSemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task A should insert data before task B's first read");

                        var count = person.Count();

                        // read 100 documents
                        count.Should().Be(data1.Length);

                        taskASemaphore.Release();
                        releasedTaskA = true;

                        (await taskBSemaphore.WaitAsync(waitTimeout))
                            .Should()
                            .BeTrue("task A should signal completion while task B is in the same transaction");

                        // keep reading 100 documents because i'm still in same transaction
                        count = person.Count();

                        count.Should().Be(data1.Length);
                    }
                    finally
                    {
                        if (!releasedTaskA)
                        {
                            taskASemaphore.Release();
                        }
                    }
                });

                await Task.WhenAll(ta, tb);
            }
        }

        [Fact]
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

        [Fact]
        public void Test_Transaction_ReleaseWhenFailToStart()
        {
            var    blockingStream             = new BlockingStream();
            var    db                         = new LiteDatabase(blockingStream) { Timeout = TimeSpan.FromSeconds(1) };
            Thread lockerThread               = null;
            try
            {
                lockerThread = new Thread(() =>
                {
                    db.GetCollection<Person>().Insert(new Person());
                    blockingStream.ShouldBlock = true;
                    db.Checkpoint();
                    db.Dispose();
                });
                lockerThread.Start();
                blockingStream.Blocked.WaitOne(1000).Should().BeTrue();
                Assert.Throws<LiteException>(() => db.GetCollection<Person>().Insert(new Person())).Message.Should().Contain("timeout");
                Assert.Throws<LiteException>(() => db.GetCollection<Person>().Insert(new Person())).Message.Should().Contain("timeout");
            }
            finally
            {
                blockingStream.ShouldUnblock.Set();
                lockerThread?.Join();
            }
        }
    }
}