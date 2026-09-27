#if DEBUG || TESTING
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The holder cleans up after an owner thread that exited while owning the mutex. A release
    /// of that same ownership on another thread (a reader disposed with its generation) may
    /// arrive meanwhile. Exactly one of the two may release the OS mutex and reopen the gate:
    /// a second gate release throws on the holder thread and terminates the process.
    /// </summary>
    public class SharedMutexOwnerExit_Tests
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

        [Fact]
        public void A_release_racing_the_exited_owner_cleanup_ends_the_ownership_once()
        {
            var name = "LiteDB-exit-" + Guid.NewGuid().ToString("N");
            using var mutex = new Mutex(false, name);
            var cleanups = 0;
            using var turn = new Mutex(false, Guid.NewGuid().ToString("N"));
            var owner = new SharedMutexOwner(mutex, new SharedMutexTurnstile(turn), () => Interlocked.Increment(ref cleanups));
            using var cleaning = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            owner.BeforeOwnerExitedCleanup = () =>
            {
                cleaning.Set();
                proceed.Wait(Prompt);
            };

            var generation = -1;
            var thread = new Thread(() =>
            {
                owner.Enter();
                generation = owner.Generation;
            });
            thread.Start();
            thread.Join(Prompt).Should().BeTrue();

            // The holder's poll finds the owner thread dead and starts its cleanup.
            cleaning.Wait(Prompt).Should().BeTrue();
            // Meanwhile a reader of that ownership is disposed on another thread.
            owner.Exit(generation);
            proceed.Set();

            owner.WaitForRelease();
            // The gate opened exactly once: this thread can own the mutex, and releasing it
            // does not find the gate already open.
            var entered = false;
            var other = new Thread(() =>
            {
                owner.Enter();
                entered = true;
                owner.Exit();
                owner.WaitForRelease();
            });
            other.Start();
            other.Join(Prompt).Should().BeTrue();
            entered.Should().BeTrue();
            cleanups.Should().Be(1);

            // Another instance can take the OS mutex: nobody holds it any more.
            var free = false;
            var probe = new Thread(() =>
            {
                using var again = new Mutex(false, name);
                free = again.WaitOne(Prompt);
                if (free) again.ReleaseMutex();
            });
            probe.Start();
            probe.Join(Prompt + Prompt).Should().BeTrue();
            free.Should().BeTrue();
            owner.BeforeOwnerExitedCleanup = null;
        }
    }
}
#endif
