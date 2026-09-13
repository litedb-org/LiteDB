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

                var taskASemaphore = new SemaphoreSlim(0, 1);
                var taskBSemaphore = new SemaphoreSlim(0, 1);

                // task A will open transaction and will insert +100 documents 
                // but will commit only 2s later
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

                // task B will try delete all documents but will be locked during 1 second
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