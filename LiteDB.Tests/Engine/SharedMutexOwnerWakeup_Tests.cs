#if DEBUG || TESTING
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The holder thread wakes on a signal or after a poll interval. A command it takes after a
    /// poll timeout, before the caller's signal arrived, must not leave that late signal set:
    /// every later wait would return at once and the holder would spin on a core until the
    /// next command or its idle exit.
    /// </summary>
    public class SharedMutexOwnerWakeup_Tests
    {
        [Fact]
        public void A_command_taken_before_its_signal_does_not_leave_the_holder_spinning()
        {
            using var mutex = new Mutex(false, "LiteDB-wakeup-" + Guid.NewGuid().ToString("N"));
            var owner = new SharedMutexOwner(mutex, () => { });
            var delayed = 0;
            // Delay the first notification past the holder's poll, so it can find the command first.
            owner.BeforeNotify = () =>
            {
                if (Interlocked.Exchange(ref delayed, 1) == 0) Thread.Sleep(80);
            };

            owner.Enter();
            try
            {
                // Holding the mutex, the holder waits for the next command.
                Thread.Sleep(200);
                Volatile.Read(ref owner.EmptySignaledWakes).Should().BeLessThan(3, "a stale signal must not keep waking the holder");
            }
            finally
            {
                owner.BeforeNotify = null;
                owner.Exit();
                owner.WaitForRelease();
            }
        }
    }
}
#endif
