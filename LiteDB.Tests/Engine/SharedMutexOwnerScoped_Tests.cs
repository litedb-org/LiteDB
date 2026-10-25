#if DEBUG || TESTING
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A scoped ownership takes the OS mutex on its own thread instead of the holder thread.
    /// It must still exclude other owners, survive ReleaseAll from another thread, refuse to
    /// be ended elsewhere without losing the mutex, and recover when its thread died.
    /// </summary>
    public class SharedMutexOwnerScoped_Tests
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

        private static string NewName() => "LiteDB-scoped-" + Guid.NewGuid().ToString("N");

        /// <summary>Whether another thread, through its own handle, can take the named mutex now.</summary>
        private static bool FreeForOthers(string name)
        {
            var free = false;
            var probe = new Thread(() =>
            {
                using var other = new Mutex(false, name);
                try
                {
                    free = other.WaitOne(0);
                    if (free) other.ReleaseMutex();
                }
                catch (AbandonedMutexException)
                {
                    free = true;
                    other.ReleaseMutex();
                }
            });
            probe.Start();
            probe.Join();
            return free;
        }

        private static void OnThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            thread.Join(Prompt).Should().BeTrue();
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        [Fact]
        public void A_scoped_ownership_holds_the_mutex_on_its_thread_without_a_holder()
        {
            var name = NewName();
            using var mutex = new Mutex(false, name);
            using var turn = new Mutex(false, NewName());
            var owner = new SharedMutexOwner(mutex, new SharedMutexTurnstile(turn), () => { });

            owner.Enter(scoped: true).Should().BeFalse();
            owner.OwnsDirectly.Should().BeTrue();
            owner.IsOwnedByCurrentThread.Should().BeTrue();
            FreeForOthers(name).Should().BeFalse();

            owner.Enter(scoped: true);
            owner.Exit();
            FreeForOthers(name).Should().BeFalse("one recursion is still open");

            owner.Exit();
            owner.OwnsDirectly.Should().BeFalse();
            FreeForOthers(name).Should().BeTrue();
            owner.HasHolderThread.Should().BeFalse("a scoped ownership needs no holder thread");
        }

        [Fact]
        public void ReleaseAll_during_a_scoped_ownership_leaves_the_release_to_its_thread()
        {
            var name = NewName();
            using var mutex = new Mutex(false, name);
            using var turn = new Mutex(false, NewName());
            var owner = new SharedMutexOwner(mutex, new SharedMutexTurnstile(turn), () => { });
            using var entered = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            var operation = new Thread(() =>
            {
                owner.Enter(scoped: true);
                entered.Set();
                released.Wait();
                // The operation ends after its connection was disposed.
                owner.Exit();
            });
            operation.Start();
            entered.Wait(Prompt).Should().BeTrue();
            var generation = owner.Generation;

            owner.ReleaseAll();
            owner.Generation.Should().NotBe(generation);
            FreeForOthers(name).Should().BeFalse("only the operation's thread can release it");

            released.Set();
            operation.Join(Prompt).Should().BeTrue();
            FreeForOthers(name).Should().BeTrue();
            // The gate reopened: this connection can own the mutex again.
            OnThread(() =>
            {
                owner.Enter(scoped: true);
                owner.Exit();
            });
        }

        [Fact]
        public void Ending_a_scoped_ownership_on_another_thread_fails_and_keeps_it()
        {
            var name = NewName();
            using var mutex = new Mutex(false, name);
            using var turn = new Mutex(false, NewName());
            var owner = new SharedMutexOwner(mutex, new SharedMutexTurnstile(turn), () => { });

            owner.Enter(scoped: true);
            var generation = owner.Generation;
            Action foreign = () => OnThread(() => owner.Exit(generation));
            foreign.Should().Throw<InvalidOperationException>();

            owner.OwnsDirectly.Should().BeTrue();
            FreeForOthers(name).Should().BeFalse();
            owner.Exit();
            FreeForOthers(name).Should().BeTrue();
        }

        [Fact]
        public void A_nested_fresh_ownership_on_a_thread_that_owns_one_directly_uses_the_holder()
        {
            using var first = new Mutex(false, NewName());
            using var second = new Mutex(false, NewName());
            using var turnA = new Mutex(false, NewName());
            var a = new SharedMutexOwner(first, new SharedMutexTurnstile(turnA), () => { });
            using var turnB = new Mutex(false, NewName());
            var b = new SharedMutexOwner(second, new SharedMutexTurnstile(turnB), () => { });

            a.Enter(scoped: true);
            try
            {
                // Another connection's named mutex may be the same OS mutex, which this
                // thread would enter recursively; the holder waits for it properly instead.
                b.Enter(scoped: true);
                b.OwnsDirectly.Should().BeFalse();
                b.IsOwnedByCurrentThread.Should().BeTrue();
                b.Exit();
                b.WaitForRelease();
            }
            finally
            {
                a.Exit();
            }
        }

        [Fact]
        public void A_scoped_owner_thread_that_died_is_recovered_as_abandoned()
        {
            var name = NewName();
            using var mutex = new Mutex(false, name);
            var exited = 0;
            using var turn = new Mutex(false, NewName());
            var owner = new SharedMutexOwner(mutex, new SharedMutexTurnstile(turn), () => Interlocked.Increment(ref exited));

            // The thread ends while owning: the OS abandons the mutex.
            OnThread(() => owner.Enter(scoped: true));

            var abandoned = false;
            OnThread(() =>
            {
                abandoned = owner.Enter(scoped: true);
                owner.Exit();
            });
            abandoned.Should().BeTrue();
            Volatile.Read(ref exited).Should().Be(1, "the connection's state of the dead owner is closed");
            FreeForOthers(name).Should().BeTrue();
        }

        [Fact]
        public void Cleanup_of_an_acquisition_ended_by_disposal_cannot_release_a_new_owner()
        {
            var name = NewName();
            using var mutex = new Mutex(false, name);
            using var turn = new Mutex(false, NewName());
            var owner = new SharedMutexOwner(mutex, new SharedMutexTurnstile(turn), () => { });
            using var acquired = new ManualResetEventSlim();
            using var cleanup = new ManualResetEventSlim();
            Exception error = null;
            var earlier = new Thread(() =>
            {
                try
                {
                    owner.Enter();
                    acquired.Set();
                    cleanup.Wait(Prompt);
                    owner.Exit(); // admission refused after Dispose, with another owner now present
                }
                catch (Exception ex) { error = ex; }
            });
            earlier.Start();
            acquired.Wait(Prompt).Should().BeTrue();
            owner.ReleaseAll();
            owner.Enter(scoped: true);
            try
            {
                cleanup.Set();
                earlier.Join(Prompt).Should().BeTrue();
                error.Should().BeNull();
                owner.OwnsDirectly.Should().BeTrue();
                FreeForOthers(name).Should().BeFalse();
            }
            finally { owner.Exit(); }
            FreeForOthers(name).Should().BeTrue();
        }
    }
}
#endif
