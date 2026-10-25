using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2814Admission_Tests
    {
        [Fact]
        public async Task Checkpoint_request_waits_for_existing_readers_instead_of_skipping()
        {
            using var locker = new LockService(new EnginePragmas((HeaderPage)null));
            locker.EnterTransaction();
            var owner = Thread.CurrentThread;
            using var acquired = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var checkpoint = Task.Run(() =>
            {
                var success = locker.TryEnterExclusive(out var mustExit, waitForReaders: true, milliseconds: 5000);
                success.Should().BeTrue();
                try { acquired.Set(); release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(); }
                finally { if (mustExit) locker.ExitExclusive(); }
            });
            try
            {
                SpinWait.SpinUntil(() => WaitingWriters(locker) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
                locker.ExitTransaction(owner);
                acquired.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                // Exclusive ownership proves the admitted checkpoint waited for
                // the old transaction instead of being skipped immediately.
                locker.TransactionsCount.Should().Be(0);
            }
            finally
            {
                locker.ExitTransaction(owner);
                release.Set();
                await checkpoint;
            }
            locker.EnterTransaction();
            locker.ExitTransaction(Thread.CurrentThread);
        }

        [Fact]
        public void Current_thread_transaction_cannot_upgrade_and_nonwaiting_call_still_skips_readers()
        {
            using var locker = new LockService(new EnginePragmas((HeaderPage)null));
            locker.EnterTransaction();
            var owner = Thread.CurrentThread;
            try
            {
                locker.TryEnterExclusive(out _, waitForReaders: true).Should().BeFalse();
                Task.Run(() => locker.TryEnterExclusive(out _)).GetAwaiter().GetResult().Should().BeFalse();
            }
            finally { locker.ExitTransaction(owner); }
        }

        [Fact]
        public async Task Other_thread_reader_times_out_and_restores_reader_admission()
        {
            using var locker = new LockService(new EnginePragmas((HeaderPage)null));
            locker.EnterTransaction();
            var owner = Thread.CurrentThread;
            try
            {
                var checkpoint = Task.Run(() =>
                {
                    var acquired = locker.TryEnterExclusive(out var mustExit, waitForReaders: true);
                    try { acquired.Should().BeFalse(); mustExit.Should().BeFalse(); }
                    finally { if (mustExit) locker.ExitExclusive(); }
                });
                (await Task.WhenAny(checkpoint, Task.Delay(2000))).Should().BeSameAs(checkpoint);
                await checkpoint;
                WaitingWriters(locker).Should().Be(0);
                await Task.Run(() =>
                {
                    locker.EnterTransaction();
                    locker.ExitTransaction(Thread.CurrentThread);
                });
            }
            finally { locker.ExitTransaction(owner); }
        }

        private static int WaitingWriters(LockService locker)
        {
            var gate = typeof(LockService).GetField("_transaction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(locker);
            var sync = typeof(TransactionGate).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(gate);
            lock (sync)
                return (int)typeof(TransactionGate).GetField("_waitingWriters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(gate);
        }
    }
}
